using System;
using System.Text.Json;
using Featureflip.Client;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// A flag type this SDK version does not know must not fail the fetch (#2395).
/// </summary>
/// <remarks>
/// The stock <c>JsonStringEnumConverter</c> throws mid-payload, so the day a new flag
/// type ships server-side every pinned SDK stops serving EVERY flag — not just the one
/// carrying the new type. Same total-outage shape <c>UnknownOperatorToleranceTests</c>
/// covers for operators (#2372), reached by an ordinary additive server change.
///
/// Tolerating it is unconditionally safe here in a way it is NOT for <c>ServeType</c> or
/// <c>ConditionLogic</c>: nothing evaluates <c>FlagConfiguration.Type</c>. It is
/// deserialized, stored, and never read — go, ruby and php do not even model the field.
/// So there is no evaluator that could mis-serve on the sentinel.
/// </remarks>
public class UnknownFlagTypeToleranceTests
{
    /// <summary>
    /// The SHIPPED options from each reader that parses flag payloads — deliberately not
    /// a locally-built replica, which would keep passing if a registration were dropped.
    /// Both transports are covered because letting them diverge is its own bug class
    /// (#2279). Constructing FeatureFlagHttpClient performs no I/O.
    /// </summary>
    public static TheoryData<string> Transports => new() { "polling", "sse" };

    private static JsonSerializerOptions OptionsFor(string transport) => transport switch
    {
        "polling" => new FeatureFlagHttpClient("test-key", new FeatureFlagOptions
        {
            BaseUrl = "http://localhost:1"
        }).JsonOptions,
        "sse" => SseStreamReader.SnapshotJsonOptions,
        _ => throw new ArgumentOutOfRangeException(nameof(transport))
    };

    private static string FlagJson(string key, string type) =>
        "{\"key\":\"" + key + "\",\"version\":1,\"type\":\"" + type + "\",\"enabled\":true,"
        + "\"variations\":[{\"key\":\"on\",\"value\":true},{\"key\":\"off\",\"value\":false}],"
        + "\"rules\":[],"
        + "\"fallthrough\":{\"type\":\"Fixed\",\"variation\":\"off\"},\"offVariation\":\"off\"}";

    [Theory]
    [MemberData(nameof(Transports))]
    public void UnknownFlagType_DeserializesToSentinel_InsteadOfThrowing(string transport)
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(
            FlagJson("f", "Timestamp"), OptionsFor(transport));

        Assert.NotNull(flag);
        Assert.Equal(TolerantFlagTypeConverter.Unrecognized, flag!.Type);
        Assert.Equal("f", flag.Key);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void KnownFlagTypes_StillDeserialize(string transport)
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(
            FlagJson("f", "Json"), OptionsFor(transport));

        Assert.Equal(FlagType.Json, flag!.Type);
    }

    /// <summary>The point of the issue: one new-typed flag must not take its neighbours down.</summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void OneUnknownFlagType_DoesNotFailTheWholePayload(string transport)
    {
        var json = "{\"flags\":[" + FlagJson("future", "Timestamp") + ","
            + FlagJson("healthy", "Boolean") + "],\"segments\":[]}";

        var response = JsonSerializer.Deserialize<GetFlagsResponse>(json, OptionsFor(transport));

        Assert.Equal(2, response!.Flags.Count);
        Assert.Equal(FlagType.Boolean, response.Flags[1].Type);
    }

    /// <summary>
    /// The OTHER axis. An integer is the server and SDK disagreeing about the wire format
    /// (#2279) and stays a wholesale rejection (#2315) — resolving it by ordinal silently
    /// shifts every later member. Tolerance is for an unknown NAME only.
    /// </summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void IntegerFlagType_IsStillRejected(string transport)
    {
        var json = "{\"key\":\"f\",\"version\":1,\"type\":0,\"enabled\":true,"
            + "\"fallthrough\":{\"type\":\"Fixed\",\"variation\":\"off\"},\"offVariation\":\"off\"}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<FlagConfiguration>(json, OptionsFor(transport)));
    }

    /// <summary>
    /// A numeric STRING is an unknown name, not an ordinal. The lookup is built from the
    /// enum's member names, so "0" misses it and degrades rather than resolving to
    /// <c>Boolean</c> the way <c>Enum.TryParse</c> would.
    /// </summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void NumericString_IsNotTreatedAsOrdinal(string transport)
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(
            FlagJson("f", "0"), OptionsFor(transport));

        Assert.Equal(TolerantFlagTypeConverter.Unrecognized, flag!.Type);
    }
}
