using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// The wire contract is that enums arrive as strings ("Boolean", "Fixed", "And").
///
/// <para><see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> takes an
/// <c>allowIntegerValues</c> parameter that defaults to <b>true</b>, and neither call site
/// overrode it — so an integer resolved to a member by its <b>underlying value</b>, which
/// for these declarations is ordinal index. That decoded correctly only for as long as the
/// .NET SDK's declaration order matched the server's. Inserting a value into the middle of
/// the server enum would have shifted every later value here to the wrong one: no
/// exception, no log, just wrong targeting decisions.</para>
///
/// <para>This is the same defect Java fixed in #2283 via
/// <c>FAIL_ON_NUMBERS_FOR_ENUMS</c> — and Java's fix comment names the .NET enum's
/// declaration order as the coupling that makes such drift invisible. C# was the
/// mirror-image case, still open (#2315). #2279 is the server bug that made integer enums
/// reach SDKs at all.</para>
///
/// <para>Every test here drives the <b>shipped</b> option sets. The pre-existing
/// <c>JsonSerializationTests</c> builds its own equivalent options, so it would have kept
/// passing throughout — a test-local replica cannot detect a configuration regression.</para>
/// </summary>
public class IntegerEnumRejectionTests
{
    private static JsonSerializerOptions PollingOptions()
    {
        // Constructing the client performs no I/O.
        using var client = new FeatureFlagHttpClient(
            "test-sdk-key", new FeatureFlagOptions { BaseUrl = "http://localhost:1" });
        return client.JsonOptions;
    }

    private static JsonSerializerOptions StreamingOptions() => SseStreamReader.SnapshotJsonOptions;

    public static TheoryData<string> BothOptionSets() => new() { "polling", "streaming" };

    private static JsonSerializerOptions Options(string which) =>
        which == "polling" ? PollingOptions() : StreamingOptions();

    // --- The contract that must keep working ---

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void StringEnums_StillDeserialize(string which)
    {
        // Guards against "fixing" the ordinal hazard by breaking the real wire format.
        var json = """
        {"key":"f","version":1,"type":"Boolean","enabled":true,
         "fallthrough":{"type":"Fixed","variation":"off"},"offVariation":"off"}
        """;

        var flag = JsonSerializer.Deserialize<FlagConfiguration>(json, Options(which));

        Assert.NotNull(flag);
        Assert.Equal(FlagType.Boolean, flag!.Type);
        Assert.Equal(ServeType.Fixed, flag.Fallthrough!.Type);
    }

    // --- Type violations must be rejected ---

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void IntegerFlagType_IsRejected(string which)
    {
        var json = """{"key":"f","version":1,"type":0,"enabled":true}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<FlagConfiguration>(json, Options(which)));
    }

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void IntegerServeType_IsRejected(string which)
    {
        var json = """{"type":0,"variation":"off"}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ServeConfig>(json, Options(which)));
    }

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void IntegerConditionLogic_IsRejected(string which)
    {
        // The #2279 shape. Ordinal 0 is `And` and 1 is `Or`, so silent acceptance here
        // flips a segment between matching ALL of its conditions and ANY of them.
        var json = """{"key":"s","version":1,"conditionLogic":1,"conditions":[]}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Segment>(json, Options(which)));
    }

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void IntegerConditionOperator_IsRejected(string which)
    {
        var json = """{"attribute":"a","operator":0,"values":["x"]}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Condition>(json, Options(which)));
    }
}
