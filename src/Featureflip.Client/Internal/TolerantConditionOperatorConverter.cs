using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Deserializes <see cref="ConditionOperator"/> without throwing on a name this SDK
/// version does not know, mapping it to <see cref="Unrecognized"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// The stock <see cref="JsonStringEnumConverter"/> throws a <see cref="JsonException"/>
/// on an unknown enum name. Because that surfaces while deserializing the whole flags
/// payload, ONE unrecognised operator in ONE condition of ONE flag failed the ENTIRE
/// fetch — every flag fell back to the caller's defaults, not just the flag carrying it
/// (#2372). The trigger is ordinary: a new operator shipped server-side reaching an SDK
/// version a customer has pinned.
/// </para>
/// <para>
/// Tolerating the value is safe because <c>FlagEvaluator</c> fails an unrecognised
/// operator CLOSED (#2262): its switch returns null for anything it has no case arm for,
/// and null is never inverted by <c>Negate</c>. The condition simply does not match, the
/// flag's other rules keep working, and every other flag is untouched.
/// </para>
/// <para>
/// Must be registered on EVERY deserializer that reads flag payloads — currently
/// <c>FeatureFlagHttpClient</c> (polling) and <c>SseStreamReader</c> (streaming). SDKs
/// treat those two as one payload shape, and letting the transports diverge is its own
/// bug class (#2279).
/// </para>
/// </remarks>
internal sealed class TolerantConditionOperatorConverter : JsonConverter<ConditionOperator>
{
    /// <summary>
    /// The value an unrecognised operator name maps to. An out-of-range cast rather than
    /// a real enum member, so no code switching on <see cref="ConditionOperator"/> can
    /// accidentally treat it as a legitimate operator.
    /// </summary>
    public const ConditionOperator Unrecognized = (ConditionOperator)(-1);

    // Derived from the enum rather than hand-listed, so adding an operator cannot leave
    // this table stale and silently demote the new operator to Unrecognized. Rejects
    // numeric strings for free, which Enum.TryParse would otherwise accept ("5" → In).
    private static readonly Dictionary<string, ConditionOperator> ByName = BuildLookup();

    private static Dictionary<string, ConditionOperator> BuildLookup()
    {
        var map = new Dictionary<string, ConditionOperator>(StringComparer.OrdinalIgnoreCase);
        foreach (ConditionOperator value in (ConditionOperator[])Enum.GetValues(typeof(ConditionOperator)))
        {
            map[value.ToString()] = value;
        }

        return map;
    }

    public override ConditionOperator Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A number is a TYPE violation and is rejected, which is a DIFFERENT axis from the
        // unknown-name tolerance above. This branch previously mapped an integer to a member
        // by ordinal, justified as "the stock converter accepted them" — but that acceptance
        // was itself the silent-wrong hazard #2315 closes, and it is now switched off
        // (allowIntegerValues: false at both registration sites). Resolving by ordinal means
        // a value inserted mid-enum server-side silently shifts every later operator here to
        // the wrong one, with no exception and no log. An unknown NAME is how a newer server
        // ships a new operator and stays tolerated; an integer is the server and SDK
        // disagreeing about the wire format, and must fail loudly (#2283 is the same fix in
        // java, #2279 the server bug that made integer enums reach SDKs at all).
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                "ConditionOperator must be a string; got a number. Enums are sent as names "
                + "on the wire, and resolving a number by ordinal silently mis-targets.");
        }

        var value = reader.GetString();
        return value != null && ByName.TryGetValue(value, out var op) ? op : Unrecognized;
    }

    public override void Write(Utf8JsonWriter writer, ConditionOperator value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(
            Enum.IsDefined(typeof(ConditionOperator), value) ? value.ToString() : "Unrecognized");
    }
}
