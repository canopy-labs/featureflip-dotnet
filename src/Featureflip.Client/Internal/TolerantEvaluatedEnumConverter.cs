using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Base for the two enums that are BOTH carried on the wire as strings AND consulted by
/// <see cref="FlagEvaluator"/>: <see cref="ServeType"/> and <see cref="ConditionLogic"/>.
/// Maps a name this SDK version does not know to an out-of-range sentinel instead of
/// throwing (#2402).
/// </summary>
/// <remarks>
/// <para>
/// Each of these dispatches on a two-way branch with no third arm — <c>serve.Type ==
/// ServeType.Fixed</c> else rollout, <c>logic == ConditionLogic.And</c> else ANY — so an
/// unrecognised value cannot be tolerated the way <see cref="TolerantFlagTypeConverter"/>
/// tolerates an unknown flag type. Tolerating it would take the ELSE arm and silently
/// bucket against weights that may not exist, or match ANY condition where the server
/// asked for ALL, so a rule fails OPEN and over-targets. Nothing evaluates
/// <c>FlagConfiguration.Type</c>, which is why that one is safe to keep.
/// </para>
/// <para>
/// Nor can it keep throwing: a <see cref="JsonException"/> here fails the WHOLE payload,
/// so one additive server change takes every flag on a pinned client down with it — the
/// #2372/#2395 outage shape, reached by an ordinary server release.
/// </para>
/// <para>
/// So the value is sentinelled here and <see cref="UnevaluableEntities"/> drops the
/// containing flag or segment at the parse boundary, leaving the rest of the payload to
/// apply. The sentinel therefore never reaches the evaluator. It is an out-of-range cast
/// rather than a real member so no <c>switch</c> can mistake it for a legitimate value,
/// and it is deliberately NOT <c>default</c>: <c>default</c> is what an ABSENT field
/// deserializes to, and absent is the missing-required-field axis, whose handling is
/// unchanged. Keeping the two tellable apart is what makes this change purely additive.
/// </para>
/// <para>
/// Must be registered on EVERY deserializer that reads flag payloads — currently
/// <c>FeatureFlagHttpClient</c> (polling) and <c>SseStreamReader</c> (streaming). SDKs
/// treat those two as one payload shape, and letting the transports diverge is its own
/// bug class (#2279).
/// </para>
/// </remarks>
internal abstract class TolerantEvaluatedEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    // Derived from the enum rather than hand-listed, so adding a member cannot leave this
    // table stale and silently demote the new value to the sentinel. Rejects numeric
    // strings for free, which Enum.TryParse would otherwise accept ("0" -> first member).
    private static readonly Dictionary<string, TEnum> ByName = BuildLookup();

    /// <summary>The value an unrecognised name maps to.</summary>
    protected abstract TEnum Unrecognized { get; }

    /// <summary>Field name used in the type-violation message, e.g. "ServeType".</summary>
    protected abstract string WireName { get; }

    private static Dictionary<string, TEnum> BuildLookup()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        foreach (TEnum value in (TEnum[])Enum.GetValues(typeof(TEnum)))
        {
            map[value.ToString()!] = value;
        }

        return map;
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A number is a TYPE violation and is rejected, which is a DIFFERENT axis from the
        // unknown-name tolerance above. Resolving by ordinal means a value inserted mid-enum
        // server-side silently shifts every later value here to the wrong one, with no
        // exception and no log (#2283/#2315; #2279 is the server bug that made integer enums
        // reach SDKs at all).
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                $"{WireName} must be a string; got a number. Enums are sent as names on the "
                + "wire, and resolving a number by ordinal silently mis-types.");
        }

        var value = reader.GetString();
        return value != null && ByName.TryGetValue(value, out var parsed) ? parsed : Unrecognized;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(
            Enum.IsDefined(typeof(TEnum), value) ? value.ToString() : "Unrecognized");
    }
}

/// <inheritdoc cref="TolerantEvaluatedEnumConverter{TEnum}"/>
internal sealed class TolerantServeTypeConverter : TolerantEvaluatedEnumConverter<ServeType>
{
    public const ServeType Unrecognised = (ServeType)(-1);

    protected override ServeType Unrecognized => Unrecognised;
    protected override string WireName => "ServeType";
}

/// <inheritdoc cref="TolerantEvaluatedEnumConverter{TEnum}"/>
internal sealed class TolerantConditionLogicConverter : TolerantEvaluatedEnumConverter<ConditionLogic>
{
    public const ConditionLogic Unrecognised = (ConditionLogic)(-1);

    protected override ConditionLogic Unrecognized => Unrecognised;
    protected override string WireName => "ConditionLogic";
}
