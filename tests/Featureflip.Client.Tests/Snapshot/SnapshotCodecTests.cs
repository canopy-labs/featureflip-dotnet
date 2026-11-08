using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

public class SnapshotCodecTests
{
    private const string Key = "sdk-key";
    private static string Hash => SnapshotCodec.ComputeKeyHash(BaseUrl, Key);

    private static bool Decode(string? text, long now, out string why, out GetFlagsResponse? data)
        => SnapshotCodec.TryDecode(text, Hash, now, null, out data, out _, out why);

    [Fact]
    public void KeyHash_IsHexSha256_OfUrlWithoutTrailingSlashesNewlineAndKey()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{BaseUrl}\n{Key}"))).ToLowerInvariant();

        Assert.Equal(expected, SnapshotCodec.ComputeKeyHash(BaseUrl, Key));
        Assert.Equal(expected, SnapshotCodec.ComputeKeyHash(BaseUrl + "///", Key));
        Assert.NotEqual(expected, SnapshotCodec.ComputeKeyHash(BaseUrl + "/other", Key));
        Assert.NotEqual(expected, SnapshotCodec.ComputeKeyHash(BaseUrl, "other-key"));
    }

    [Fact]
    public void Envelope_NeverContainsTheSdkKey()
    {
        var text = Envelope(Key, Now());

        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public void ValidEnvelope_Decodes()
    {
        var now = Now();
        Assert.True(Decode(Envelope(Key, now - 5000), now, out var why, out var data), why);
        Assert.Equal("flag-a", Assert.Single(data!.Flags).Key);
    }

    private static string Raw(string hash, long now, string dataJson)
        => "{\"v\":1,\"keyHash\":\"" + hash + "\",\"fetchedAt\":" + now + ",\"data\":" + dataJson + "}";

    public static IEnumerable<object[]> InvalidTexts()
    {
        var now = Now();
        var hash = SnapshotCodec.ComputeKeyHash(BaseUrl, Key);
        var data = FlagsJson();
        yield return new object[] { "", "empty text" };
        yield return new object[] { "{not json", "not valid JSON" };
        yield return new object[] { "[1,2]", "not a snapshot envelope" };
        yield return new object[] { "\"text\"", "not a snapshot envelope" };
        yield return new object[] { $$"""{"v":2,"keyHash":"{{hash}}","fetchedAt":{{now}},"data":{{data}}}""", "unsupported envelope version" };
        yield return new object[] { $$"""{"v":"1","keyHash":"{{hash}}","fetchedAt":{{now}},"data":{{data}}}""", "unsupported envelope version" };
        yield return new object[] { $$"""{"keyHash":"{{hash}}","fetchedAt":{{now}},"data":{{data}}}""", "unsupported envelope version" };
        yield return new object[] { $$"""{"v":1,"keyHash":"nope","fetchedAt":{{now}},"data":{{data}}}""", "different SDK key" };
        yield return new object[] { $$"""{"v":1,"fetchedAt":{{now}},"data":{{data}}}""", "different SDK key" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":0,"data":{{data}}}""", "positive integer" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":-5,"data":{{data}}}""", "positive integer" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":1.5,"data":{{data}}}""", "positive integer" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":"{{now}}","data":{{data}}}""", "positive integer" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","data":{{data}}}""", "positive integer" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":{{now}}}""", "missing data" };
        yield return new object[] { $$"""{"v":1,"keyHash":"{{hash}}","fetchedAt":{{now}},"data":[]}""", "missing data" };
        yield return new object[] { Raw(hash, now, "{}"), "payload validation" };
        yield return new object[] { Raw(hash, now, """{"flags":{},"segments":[]}"""), "payload validation" };
        yield return new object[] { Raw(hash, now, """{"flags":[{"key":"f","type":3}],"segments":[]}"""), "payload validation" };
    }

    [Theory]
    [MemberData(nameof(InvalidTexts))]
    public void InvalidText_IsRejectedWithTheNameOfTheCheck(string text, string expectedWhy)
    {
        Assert.False(Decode(text, Now(), out var why, out _));
        Assert.Contains(expectedWhy, why, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchedAt_Within60SecondsAhead_IsAccepted_And61IsInvalid()
    {
        const long now = 1_800_000_000_000;
        Assert.True(Decode(Envelope(Key, now + 60_000), now, out var why, out _), why);
        Assert.False(Decode(Envelope(Key, now + 60_001), now, out why, out _));
        Assert.Contains("future", why, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeNumberSpelledAsFloat_IsAcceptedAsFetchedAt()
    {
        var now = Now();
        var text = Envelope(Key, now).Replace($"\"fetchedAt\":{now}", $"\"fetchedAt\":{now}.0");
        Assert.True(Decode(text, now, out var why, out _), why);
    }

    // A rich payload: rollout serve, condition groups with Or, a segment, a null variation value,
    // a prerequisite, a JSON-valued and a string flag.
    private const string RichPayload = """
        {"environment":"prod","version":7,
         "flags":[
          {"key":"zeta","version":3,"type":"String","enabled":true,
           "variations":[{"key":"a","value":"x"},{"key":"b","value":null}],
           "rules":[{"id":"r1","priority":1,
             "conditionGroups":[{"operator":"Or","conditions":[{"attribute":"plan","operator":"In","values":["pro","team"],"negate":false},{"attribute":"v","operator":"SemverGreaterThan","values":["1.2.0"],"negate":true}]}],
             "serve":{"type":"Rollout","bucketBy":"user_id","salt":"s","variations":[{"key":"a","weight":50},{"key":"b","weight":50}]}},
            {"id":"r2","priority":2,"segmentKey":"seg","serve":{"type":"Fixed","variation":"a"}}],
           "fallthrough":{"type":"Fixed","variation":"b"},"offVariation":"b",
           "prerequisites":[{"prerequisiteFlagKey":"alpha","expectedVariationKey":"on"}]},
          {"key":"alpha","version":1,"type":"Json","enabled":false,
           "variations":[{"key":"on","value":{"k":[1,2,{"z":true}]}},{"key":"off","value":{}}],
           "rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}],
         "segments":[{"key":"seg","version":2,"conditionLogic":"Or","conditions":[{"attribute":"email","operator":"EndsWith","values":["@x.io"],"negate":false}]},
                     {"key":"aaa","version":1,"conditionLogic":"And","conditions":[]}]}
        """;

    private static GetFlagsResponse Parse(string json)
        => JsonSerializer.Deserialize<GetFlagsResponse>(json, SseStreamReader.SnapshotJsonOptions)!;

    [Fact]
    public void Serialization_UsesRealWireSpelling_AndSortsByKey()
    {
        var parsed = Parse(RichPayload);
        var text = SnapshotCodec.SerializeState(parsed.Environment, parsed.Version, parsed.Flags, parsed.Segments);

        Assert.Contains("\"type\":\"String\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"Json\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"Rollout\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"Fixed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"operator\":\"Or\"", text, StringComparison.Ordinal);
        Assert.Contains("\"operator\":\"SemverGreaterThan\"", text, StringComparison.Ordinal);
        Assert.Contains("\"conditionLogic\":\"Or\"", text, StringComparison.Ordinal);
        Assert.Contains("\"conditionLogic\":\"And\"", text, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(text);
        var flagKeys = doc.RootElement.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToArray();
        var segmentKeys = doc.RootElement.GetProperty("segments").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "zeta" }, flagKeys);
        Assert.Equal(new[] { "aaa", "seg" }, segmentKeys);
    }

    [Fact]
    public void Serialization_IsIndependentOfInputOrder()
    {
        var parsed = Parse(RichPayload);
        var a = SnapshotCodec.SerializeState("prod", 7, parsed.Flags, parsed.Segments);
        var b = SnapshotCodec.SerializeState("prod", 7, Enumerable.Reverse(parsed.Flags), Enumerable.Reverse(parsed.Segments));

        Assert.Equal(a, b);
    }

    [Fact]
    public void RoundTrip_ThroughTheSdkParser_ReproducesTheSameState()
    {
        var parsed = Parse(RichPayload);
        var first = SnapshotCodec.SerializeState(parsed.Environment, parsed.Version, parsed.Flags, parsed.Segments);

        var now = Now();
        var envelope = SnapshotCodec.EncodeEnvelope(Hash, now, first);
        Assert.True(Decode(envelope, now, out var why, out var data), why);

        Assert.Equal("prod", data!.Environment);
        Assert.Equal(7, data.Version);
        var second = SnapshotCodec.SerializeState(data.Environment, data.Version, data.Flags, data.Segments);
        Assert.Equal(first, second);

        var zeta = data.Flags.Single(f => f.Key == "zeta");
        Assert.Equal(FlagType.String, zeta.Type);
        Assert.Equal(ServeType.Rollout, zeta.Rules[0].Serve!.Type);
        Assert.Equal(ConditionLogic.Or, zeta.Rules[0].ConditionGroups[0].Operator);
        Assert.Equal(ConditionOperator.SemverGreaterThan, zeta.Rules[0].ConditionGroups[0].Conditions[1].Operator);
        Assert.True(zeta.Rules[0].ConditionGroups[0].Conditions[1].Negate);
        Assert.Equal("alpha", zeta.Prerequisites[0].PrerequisiteFlagKey);
        Assert.Equal(ConditionLogic.Or, data.Segments.Single(s => s.Key == "seg").ConditionLogic);
    }

    [Fact]
    public void RoundTrip_ServesTheSameValuesThroughTheEvaluator()
    {
        var parsed = Parse(RichPayload);
        var text = SnapshotCodec.SerializeState(parsed.Environment, parsed.Version, parsed.Flags, parsed.Segments);
        var now = Now();
        Assert.True(Decode(SnapshotCodec.EncodeEnvelope(Hash, now, text), now, out _, out var data));

        var evaluator = new FlagEvaluator();
        var context = new EvaluationContext { UserId = "u-1" };
        foreach (var (before, after) in parsed.Flags.OrderBy(f => f.Key).Zip(data!.Flags.OrderBy(f => f.Key)))
        {
            var a = evaluator.Evaluate(before, context, parsed.Flags.ToDictionary(f => f.Key), _ => null);
            var b = evaluator.Evaluate(after, context, data.Flags.ToDictionary(f => f.Key), _ => null);
            Assert.Equal(a.VariationKey, b.VariationKey);
            Assert.Equal(a.Reason, b.Reason);
        }
    }

    [Fact]
    public void NullVariationValue_SurvivesTheRoundTripAsNull()
    {
        var parsed = Parse(RichPayload);
        var text = SnapshotCodec.SerializeState(parsed.Environment, parsed.Version, parsed.Flags, parsed.Segments);

        using var doc = JsonDocument.Parse(text);
        var zeta = doc.RootElement.GetProperty("flags").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "zeta");
        var b = zeta.GetProperty("variations").EnumerateArray().Single(v => v.GetProperty("key").GetString() == "b");
        Assert.Equal(JsonValueKind.Null, b.GetProperty("value").ValueKind);
    }

    [Fact]
    public void EntitiesThisBuildCannotEvaluate_AreDroppedOnRead()
    {
        var now = Now();
        var data = """{"environment":"t","version":1,"flags":[{"key":"bad","version":1,"type":"Boolean","enabled":true,"variations":[],"rules":[],"fallthrough":{"type":"FromTheFuture","variation":"x"},"offVariation":"x"}],"segments":[]}""";
        Assert.True(Decode(Envelope(Key, now, data), now, out var why, out var parsed), why);
        Assert.Empty(parsed!.Flags);
    }
}
