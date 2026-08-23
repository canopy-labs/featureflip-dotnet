using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// An operator name this SDK version does not know must degrade ONE condition, not the
/// whole payload (#2372).
/// </summary>
/// <remarks>
/// The stock <c>JsonStringEnumConverter</c> throws mid-payload, so a single unrecognised
/// operator in a single condition failed the entire flags fetch and every flag fell back
/// to the caller's defaults. The trigger is ordinary: a new operator shipped server-side
/// reaching a pinned SDK version.
///
/// The companion rule lives in <c>FlagEvaluatorTests</c>: an unrecognised operator fails
/// CLOSED (#2262), so tolerating it here cannot become a match-everyone.
/// </remarks>
public class UnknownOperatorToleranceTests
{
    /// <summary>
    /// The SHIPPED options from each reader that parses flag payloads — deliberately not
    /// a locally-built replica. The subject under test is how the real readers are
    /// configured, and a replica would keep passing if a registration were dropped.
    ///
    /// Both transports are covered because SDKs treat GET /v1/sdk/flags and the SSE
    /// `sync` snapshot as one payload shape; letting the two diverge is its own bug
    /// class (#2279). Constructing FeatureFlagHttpClient performs no I/O.
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

    // Plain concatenation rather than a raw string literal: the JSON is dense with
    // consecutive closing braces, which fight interpolated-raw-string brace counting.
    private static string FlagJson(string key, string op) =>
        "{\"key\":\"" + key + "\",\"version\":1,\"type\":\"Boolean\",\"enabled\":true,"
        + "\"variations\":[{\"key\":\"on\",\"value\":true},{\"key\":\"off\",\"value\":false}],"
        + "\"rules\":[{\"id\":\"r1\",\"priority\":0,"
        + "\"conditionGroups\":[{\"operator\":\"And\",\"conditions\":["
        + "{\"attribute\":\"country\",\"operator\":\"" + op + "\",\"values\":[\"US\"],\"negate\":true}]}],"
        + "\"serve\":{\"type\":\"Fixed\",\"variation\":\"on\"}}],"
        + "\"fallthrough\":{\"type\":\"Fixed\",\"variation\":\"off\"},\"offVariation\":\"off\"}";

    [Theory]
    [MemberData(nameof(Transports))]
    public void UnknownOperator_DeserializesToSentinel_InsteadOfThrowing(string transport)
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(
            FlagJson("f", "SomeFutureOperator"), OptionsFor(transport));

        Assert.NotNull(flag);
        Assert.Equal(
            TolerantConditionOperatorConverter.Unrecognized,
            flag!.Rules[0].ConditionGroups[0].Conditions[0].Operator);
    }

    // Asserted by NAME rather than by enum value: ConditionOperator is internal, so it
    // cannot appear in a public test signature. Comparing ToString() also proves the
    // value is a real defined member, not the out-of-range sentinel.
    [Theory]
    [InlineData("Equals")]
    [InlineData("NotIn")]
    [InlineData("SemverLessThanOrEqual")]
    public void KnownOperators_StillDeserialize(string wire)
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(FlagJson("f", wire), OptionsFor("polling"));

        Assert.NotNull(flag);
        var parsed = flag!.Rules[0].ConditionGroups[0].Conditions[0].Operator;
        Assert.Equal(wire, parsed.ToString());
        Assert.NotEqual(TolerantConditionOperatorConverter.Unrecognized, parsed);
    }

    /// <summary>The point of the issue: one bad flag must not take its neighbours down.</summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void OneUnknownOperator_DoesNotFailTheWholePayload(string transport)
    {
        var json = "{\"flags\":["
            + FlagJson("broken", "SomeFutureOperator") + ","
            + FlagJson("healthy", "Equals")
            + "],\"segments\":[]}";

        var response = JsonSerializer.Deserialize<GetFlagsResponse>(json, OptionsFor(transport));

        Assert.NotNull(response);
        Assert.Equal(2, response!.Flags.Count);
        var healthy = response.Flags.Single(f => f.Key == "healthy");
        Assert.Equal("Equals", healthy.Rules[0].ConditionGroups[0].Conditions[0].Operator.ToString());
    }

    // A numeric string must NOT be accepted as an ordinal — Enum.TryParse would turn
    // "5" into a real operator nobody sent.
    [Fact]
    public void NumericString_IsNotTreatedAsOrdinal()
    {
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(FlagJson("f", "5"), OptionsFor("polling"));

        Assert.NotNull(flag);
        Assert.Equal(
            TolerantConditionOperatorConverter.Unrecognized,
            flag!.Rules[0].ConditionGroups[0].Conditions[0].Operator);
    }
}
