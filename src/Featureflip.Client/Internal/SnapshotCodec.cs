using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// The stored text: <c>{"v":1,"keyHash":…,"fetchedAt":&lt;epoch ms&gt;,"data":&lt;flags payload&gt;}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>data</c> is the full flag state in the wire shape of <c>GET /v1/sdk/flags</c>, written
/// with the same converters the SDK parses with, so enums keep their real PascalCase spelling
/// (<c>Boolean</c>, <c>Fixed</c>, <c>And</c>) and the text reads back through the SDK's own
/// parser. Flags and segments are sorted by key before writing, because the "unchanged state"
/// comparison that throttles refresh writes compares serialized text.
/// </para>
/// <para>
/// The envelope frame is composed by hand around the generated <c>data</c> text (as the Node
/// and Python SDKs do) so the state is serialized once. It is reflection-free: the frame is
/// a fixed string and the payload goes through <see cref="FeatureflipJsonContext"/>.
/// </para>
/// <para>
/// An unknown flag type, condition operator, serve type or condition logic cannot be kept
/// verbatim the way the other SDKs keep an unknown flag type: this SDK's tolerant converters
/// map the name to a sentinel, so it is written as <c>Unrecognized</c>. Entities carrying
/// an unevaluable serve type or condition logic are dropped before they reach the store, so
/// they are never persisted either.
/// </para>
/// </remarks>
internal static class SnapshotCodec
{
    /// <summary>How far in the future a stored <c>fetchedAt</c> may be (writer/reader clock skew).</summary>
    public const long FutureFetchedAtToleranceMs = 60_000;

    public const string ReasonSnapshot = StartupInfo.ReasonSnapshot;
    public const string ReasonExpired = StartupInfo.ReasonSnapshotExpired;
    public const string ReasonInvalid = StartupInfo.ReasonSnapshotInvalid;

    // Reads the persisted payload exactly as the sync frame is read (SseStreamReader).
    private static readonly FeatureflipJsonContext ReadJson = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new TolerantConditionOperatorConverter(),
            new TolerantFlagTypeConverter(),
            new TolerantServeTypeConverter(),
            new TolerantConditionLogicConverter()
        }
    });

    // Same converters for writing. A variation value of JSON null deserializes to an Undefined
    // element, which JsonElement.WriteTo refuses, so it is written as null (see FlagStore).
    private static readonly FeatureflipJsonContext WriteJson = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new TolerantConditionOperatorConverter(),
            new TolerantFlagTypeConverter(),
            new TolerantServeTypeConverter(),
            new TolerantConditionLogicConverter(),
            new FlagStore.UndefinedTolerantJsonElementConverter()
        }
    });

    /// <summary>Hex SHA-256 of the base URL (trailing slashes stripped), a newline and the SDK key.</summary>
    public static string ComputeKeyHash(string baseUrl, string sdkKey)
    {
        var bytes = Encoding.UTF8.GetBytes(baseUrl.TrimEnd('/') + "\n" + sdkKey);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>The wire payload for a state, flags and segments sorted by key.</summary>
    public static string SerializeState(
        string environment,
        int version,
        IEnumerable<FlagConfiguration> flags,
        IEnumerable<Segment> segments)
    {
        var response = new GetFlagsResponse
        {
            Environment = environment,
            Version = version,
            Flags = flags.OrderBy(f => f.Key, StringComparer.Ordinal).ToList(),
            Segments = segments.OrderBy(s => s.Key, StringComparer.Ordinal).ToList()
        };
        return JsonSerializer.Serialize(response, WriteJson.GetFlagsResponse);
    }

    public static string EncodeEnvelope(string keyHash, long fetchedAtMs, string dataText)
        => "{\"v\":1,\"keyHash\":\"" + keyHash + "\",\"fetchedAt\":"
           + fetchedAtMs.ToString(CultureInfo.InvariantCulture) + ",\"data\":" + dataText + "}";

    /// <summary>
    /// Parses and checks stored text in the pinned order. <paramref name="why"/> names the
    /// failed check and never includes contents or the key. Age is not checked here.
    /// </summary>
    public static bool TryDecode(
        string? text,
        string keyHash,
        long nowMs,
        ILogger? logger,
        out GetFlagsResponse? data,
        out long fetchedAtMs,
        out string why)
    {
        data = null;
        fetchedAtMs = 0;

        if (text == null)
        {
            why = "store returned no text";
            return false;
        }

        if (text.Length == 0)
        {
            why = "empty text";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (Exception)
        {
            // JsonException for malformed JSON, but also ArgumentException for text a custom store
            // handed back that is not valid UTF-16 (a lone surrogate), so catch them all.
            why = "not valid JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                why = "not a snapshot envelope";
                return false;
            }

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetDouble(out var version) || version != 1)
            {
                why = "unsupported envelope version";
                return false;
            }

            if (!root.TryGetProperty("keyHash", out var hash) || hash.ValueKind != JsonValueKind.String
                || !string.Equals(hash.GetString(), keyHash, StringComparison.Ordinal))
            {
                why = "written for a different SDK key or base URL";
                return false;
            }

            if (!root.TryGetProperty("fetchedAt", out var fetched) || !TryPositiveInteger(fetched, out fetchedAtMs))
            {
                why = "fetchedAt is not a positive integer";
                return false;
            }

            if (fetchedAtMs > nowMs + FutureFetchedAtToleranceMs)
            {
                why = "fetchedAt is in the future";
                return false;
            }

            if (!root.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                why = "missing data";
                return false;
            }

            try
            {
                if (!payload.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Array
                    || !payload.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("flags and segments must be arrays");
                }

                var parsed = JsonSerializer.Deserialize(payload.GetRawText(), ReadJson.GetFlagsResponse)
                    ?? throw new JsonException("payload is null");
                data = UnevaluableEntities.DropUnevaluable(parsed, logger);
            }
            catch (Exception)
            {
                why = "data failed payload validation";
                return false;
            }
        }

        why = string.Empty;
        return true;
    }

    private static bool TryPositiveInteger(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt64(out var integer))
        {
            value = integer;
            return integer > 0;
        }

        // 1.7e12 style spellings of a whole number are accepted, a fractional value is not.
        if (element.TryGetDouble(out var number) && number > 0 && number < long.MaxValue
            && Math.Floor(number) == number)
        {
            value = (long)number;
            return true;
        }

        return false;
    }
}
