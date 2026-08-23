using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Deserializes <see cref="FlagType"/> without throwing on a name this SDK version does
/// not know, mapping it to <see cref="Unrecognized"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// The stock <see cref="JsonStringEnumConverter"/> throws a <see cref="JsonException"/>
/// on an unknown enum name. Because that surfaces while deserializing the whole flags
/// payload, the day a new flag type ships server-side EVERY pinned SDK stops serving
/// EVERY flag — not just the one carrying the new type (#2395). Same total-outage shape
/// <see cref="TolerantConditionOperatorConverter"/> exists to prevent (#2372), reached by
/// an ordinary additive server change.
/// </para>
/// <para>
/// Tolerating the value is unconditionally safe here, and for a stronger reason than the
/// operator case: nothing evaluates <c>FlagConfiguration.Type</c>. It is deserialized,
/// stored on the DTO, and never read — <c>FlagEvaluator</c> does not consult it, and the
/// go, ruby and php SDKs do not model the field at all. So the sentinel cannot reach any
/// dispatch, which is why this needs no fail-closed companion the way the operator does.
/// </para>
/// <para>
/// It is also why this tolerance does NOT extend to <see cref="ServeType"/> or
/// <see cref="ConditionLogic"/>, which ARE consulted and each dispatch on a two-way
/// branch — <c>serve.Type == ServeType.Fixed</c> else rollout, <c>logic ==
/// ConditionLogic.And</c> else ANY. A sentinel there would fall to the else arm and
/// silently mis-target rather than fail. Those two need an entity-level drop instead
/// (follow-up to #2395); until then they keep failing loudly.
/// </para>
/// <para>
/// Must be registered on EVERY deserializer that reads flag payloads — currently
/// <c>FeatureFlagHttpClient</c> (polling) and <c>SseStreamReader</c> (streaming). SDKs
/// treat those two as one payload shape, and letting the transports diverge is its own
/// bug class (#2279).
/// </para>
/// </remarks>
internal sealed class TolerantFlagTypeConverter : JsonConverter<FlagType>
{
    /// <summary>
    /// The value an unrecognised flag type name maps to. An out-of-range cast rather than
    /// a real enum member, so no code switching on <see cref="FlagType"/> can accidentally
    /// treat it as a legitimate type.
    /// </summary>
    public const FlagType Unrecognized = (FlagType)(-1);

    // Derived from the enum rather than hand-listed, so adding a flag type cannot leave
    // this table stale and silently demote the new type to Unrecognized. Rejects numeric
    // strings for free, which Enum.TryParse would otherwise accept ("0" → Boolean).
    private static readonly Dictionary<string, FlagType> ByName = BuildLookup();

    private static Dictionary<string, FlagType> BuildLookup()
    {
        var map = new Dictionary<string, FlagType>(StringComparer.OrdinalIgnoreCase);
        foreach (FlagType value in (FlagType[])Enum.GetValues(typeof(FlagType)))
        {
            map[value.ToString()] = value;
        }

        return map;
    }

    public override FlagType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A number is a TYPE violation and is rejected, which is a DIFFERENT axis from the
        // unknown-name tolerance above. Resolving by ordinal means a value inserted mid-enum
        // server-side silently shifts every later type here to the wrong one, with no
        // exception and no log. An unknown NAME is how a newer server ships a new flag type
        // and stays tolerated; an integer is the server and SDK disagreeing about the wire
        // format, and must fail loudly (#2283 is the same fix in java, #2279 the server bug
        // that made integer enums reach SDKs at all).
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                "FlagType must be a string; got a number. Enums are sent as names on the "
                + "wire, and resolving a number by ordinal silently mis-types.");
        }

        var value = reader.GetString();
        return value != null && ByName.TryGetValue(value, out var type) ? type : Unrecognized;
    }

    public override void Write(Utf8JsonWriter writer, FlagType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(
            Enum.IsDefined(typeof(FlagType), value) ? value.ToString() : "Unrecognized");
    }
}
