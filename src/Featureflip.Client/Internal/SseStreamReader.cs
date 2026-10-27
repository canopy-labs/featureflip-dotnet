using System.Text;
using System.Text.Json;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// Reads Server-Sent Events (SSE) from a stream and parses flag update messages.
/// </summary>
internal sealed class SseStreamReader : IDisposable
{
    // Deserializer for the `sync` snapshot payload — same shape/options as the
    // GET /v1/sdk/flags response (camelCase properties, string enums), bound to the
    // source-generated context so the frame parses with reflection disabled (#4301).
    private static readonly FeatureflipJsonContext SnapshotJson = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // An options-registered converter outranks the generated default, and these four
        // claim every enum on the wire. Each maps an unknown NAME to a sentinel instead of
        // throwing, which previously failed the WHOLE flags payload over one condition
        // (#2372, #2402).
        //
        // Each also rejects an INTEGER, which is the OTHER axis, and the two must not be
        // confused. An integer resolved to a member by its UNDERLYING VALUE — ordinal, for
        // these declarations — so a value inserted mid-enum server-side would silently
        // shift every later value here to the wrong one: no exception, no log, wrong
        // targeting. Java closed the identical hazard with FAIL_ON_NUMBERS_FOR_ENUMS
        // (#2283); C# was the mirror-image case (#2315). #2279 is the server bug that made
        // integer enums reach SDKs at all.
        //
        // So: an unknown enum STRING is tolerated (forward compatibility, #2372); an
        // integer is a TYPE violation and is rejected.
        //
        // No stock JsonStringEnumConverter after them: it was unreachable, and its
        // non-generic form needs runtime code generation NativeAOT lacks (#4301).
        Converters =
        {
            new TolerantConditionOperatorConverter(),
            new TolerantFlagTypeConverter(),
            new TolerantServeTypeConverter(),
            new TolerantConditionLogicConverter()
        }
    });

    // internal (not private) so tests can assert against the SHIPPED configuration rather
    // than a replica, which would keep passing if this registration regressed.
    internal static JsonSerializerOptions SnapshotJsonOptions => SnapshotJson.Options;

    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly StreamReader _reader;
    private bool _disposed;

    public SseStreamReader(Stream stream, ILogger logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>
    /// Reads SSE messages from the stream and yields parsed events.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var eventType = string.Empty;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested && !_disposed)
        {
            string? line;
            try
            {
#if NETSTANDARD2_0
                line = await _reader.ReadLineAsync().ConfigureAwait(false);
#else
                line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
#endif
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (IOException ex)
            {
                // Debug, not Warning: a long-lived stream severed mid-frame is routine
                // behind a CDN or proxy (#2457) — the core reconnects and the server
                // replays a full `sync` snapshot, so no config is missed and nothing is
                // degraded. The reader cannot tell a routine sever from a stream that
                // never works; the core can, and warns for that case.
                _logger.LogDebug(ex, "SSE stream read error");
                yield break;
            }

            if (line == null)
            {
                // End of stream
                yield break;
            }

            if (string.IsNullOrEmpty(line))
            {
                // Empty line = end of event
                if (dataBuilder.Length > 0)
                {
                    var data = dataBuilder.ToString();
                    dataBuilder.Clear();

                    var evt = ParseEvent(eventType, data);
                    if (evt != null)
                    {
                        yield return evt;
                    }
                }
                eventType = string.Empty;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }
                dataBuilder.Append(line.Substring(5).Trim());
            }
            else if (line.StartsWith(":", StringComparison.Ordinal))
            {
                // Comment/keepalive, ignore
            }
        }
    }

    private SseEvent? ParseEvent(string eventType, string data)
    {
        try
        {
            return eventType switch
            {
                "flag.created" => ParseFlagEvent(data, SseEventType.FlagCreated),
                "flag.updated" => ParseFlagEvent(data, SseEventType.FlagUpdated),
                "flag.deleted" => ParseFlagEvent(data, SseEventType.FlagDeleted),
                "segment.updated" => new SseEvent { Type = SseEventType.SegmentUpdated },
                "sync" => ParseSyncEvent(data),
                "ping" => new SseEvent { Type = SseEventType.Ping },
                _ => null
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse SSE event: {EventType}", eventType);
            return null;
        }
    }

    private static SseEvent ParseFlagEvent(string data, SseEventType type)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : (int?)null;
        return new SseEvent { Type = type, FlagKey = key, Version = version };
    }

    private SseEvent ParseSyncEvent(string data)
    {
        // Full config snapshot the server sends on (re)connect. Carried whole so
        // the core can apply it as a store REPLACE (drop flags absent from it),
        // re-syncing anything changed/deleted during a disconnect.
        var snapshot = JsonSerializer.Deserialize(data, SnapshotJson.GetFlagsResponse);
        // The sync frame is parsed here rather than through GetFlagsAsync, so the
        // entity-drop has to be applied explicitly — letting the two transports diverge on
        // the same payload shape is its own bug class (#2279).
        if (snapshot != null)
        {
            snapshot = UnevaluableEntities.DropUnevaluable(snapshot, _logger);
        }
        return new SseEvent { Type = SseEventType.Sync, Snapshot = snapshot };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
        _stream.Dispose();
    }
}

internal enum SseEventType
{
    FlagCreated,
    FlagUpdated,
    FlagDeleted,
    SegmentUpdated,
    Sync,
    Ping
}

internal sealed class SseEvent
{
    public SseEventType Type { get; init; }
    public string? FlagKey { get; init; }
    public int? Version { get; init; }
    public GetFlagsResponse? Snapshot { get; init; }
}
