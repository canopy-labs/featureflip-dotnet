using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Featureflip.Client.ReflectionFreeTests;

/// <summary>
/// Drives a REAL <see cref="FeatureflipClient"/> (not a for-testing stub, which skips both the
/// fetch and the event post) in a process where reflection-based System.Text.Json is disabled,
/// as it is in NativeAOT, trimmed and .NET 10 file-based apps (#4301).
/// </summary>
/// <remarks>
/// Before the source-generated context, every flag fetch and event post here threw
/// <see cref="InvalidOperationException"/>: the wait never completed, every read served its
/// default, and every event was dropped — silently, under the default NullLogger.
/// </remarks>
public class ReflectionFreeClientTests
{
    private const string RuleId = "7f3c2b1a-0d4e-4c5b-9a8f-1e2d3c4b5a69";

    // Deliberately omits `prerequisites` on every flag and `segmentKey` on the rule: an
    // absent property must keep its initializer, not be overwritten with null, which is how
    // the source generator treats init-only properties (#4301).
    private static readonly string PolledFlags = """
        {"environment":"test","version":1,"segments":[],"flags":[
          {"key":"bool-flag","version":1,"type":"Boolean","enabled":true,
           "variations":[{"key":"on","value":true},{"key":"off","value":false}],
           "rules":[{"id":"7f3c2b1a-0d4e-4c5b-9a8f-1e2d3c4b5a69","priority":0,
             "conditionGroups":[{"operator":"And","conditions":[
               {"attribute":"plan","operator":"Equals","values":["pro"],"negate":false}]}],
             "serve":{"type":"Fixed","variation":"on"}}],
           "fallthrough":{"type":"Fixed","variation":"off"},"offVariation":"off"},
          {"key":"string-flag","version":1,"type":"String","enabled":true,
           "variations":[{"key":"a","value":"hello"},{"key":"b","value":"bye"}],
           "rules":[],"fallthrough":{"type":"Fixed","variation":"a"},"offVariation":"b"},
          {"key":"number-flag","version":1,"type":"Number","enabled":true,
           "variations":[{"key":"n","value":42},{"key":"z","value":0}],
           "rules":[],"fallthrough":{"type":"Fixed","variation":"n"},"offVariation":"z"},
          {"key":"json-flag","version":1,"type":"Json","enabled":true,
           "variations":[{"key":"j","value":{"color":"blue","size":3}},{"key":"e","value":{}}],
           "rules":[],"fallthrough":{"type":"Fixed","variation":"j"},"offVariation":"e"}
        ]}
        """;

    [Fact]
    public void ReflectionBasedSerializationIsDisabledInThisProcess()
    {
        // Every other test here proves nothing unless the switch actually reached the host.
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public async Task Polling_InitializesAndServesEveryAccessorType()
    {
        await using var server = new StubFlagServer(PolledFlags);
        using var client = server.CreateClient(streaming: false);

        await client.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(client.IsInitialized);

        var ctx = new EvaluationContext { UserId = "user-1" }.Set("plan", "pro");

        var boolDetail = client.VariationDetail("bool-flag", ctx, false);
        Assert.True(boolDetail.Value);
        Assert.Equal(EvaluationReason.RuleMatch, boolDetail.Reason);

        Assert.Equal("hello", client.StringVariation("string-flag", ctx, "default"));
        Assert.Equal(42, client.IntVariation("number-flag", ctx, -1));
        Assert.Equal(42.0, client.DoubleVariation("number-flag", ctx, -1));
        Assert.Equal(42L, client.Variation("number-flag", ctx, -1L));
        Assert.Equal(42m, client.Variation("number-flag", ctx, -1m));

        var element = client.VariationDetail("json-flag", ctx, default(JsonElement));
        Assert.Equal(EvaluationReason.Fallthrough, element.Reason);
        Assert.Equal("blue", element.Value.GetProperty("color").GetString());

        var node = client.JsonVariation<JsonNode?>("json-flag", ctx, null);
        Assert.Equal(3, node!["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task CallerPocoWithoutMetadata_DegradesToDefaultWithErrorReason()
    {
        await using var server = new StubFlagServer(PolledFlags);
        using var client = server.CreateClient(streaming: false);
        await client.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var fallback = new Shape { Color = "fallback" };
        var detail = client.VariationDetail("json-flag", new EvaluationContext { UserId = "u" }, fallback);

        // No generated metadata for a caller's own type, and no reflection to build it: the
        // read must degrade like any other conversion failure, never throw.
        Assert.Same(fallback, detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
        Assert.Contains("reflection-based JSON serialization is disabled", detail.ErrorMessage);
    }

    [Fact]
    public async Task Flush_PostsEvaluationEventWithWireFields()
    {
        await using var server = new StubFlagServer(PolledFlags);
        using var client = server.CreateClient(streaming: false);
        await client.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var ctx = new EvaluationContext { UserId = "user-1" }.Set("plan", "pro");
        Assert.True(client.BoolVariation("bool-flag", ctx, false));

        var evt = Assert.Single(await FlushUntilEventsAsync(client, server));
        Assert.Equal("Evaluation", evt.GetProperty("type").GetString());
        Assert.Equal("bool-flag", evt.GetProperty("flagKey").GetString());
        Assert.Equal("user-1", evt.GetProperty("userId").GetString());
        Assert.Equal("on", evt.GetProperty("variation").GetString());
        Assert.Equal("rule-match", evt.GetProperty("reason").GetString());
        Assert.Equal(RuleId, evt.GetProperty("ruleId").GetString());
        Assert.Equal(JsonValueKind.String, evt.GetProperty("timestamp").ValueKind);
        // Null optionals stay off the wire, as on the reflection path.
        Assert.False(evt.TryGetProperty("prerequisiteKey", out _));
        Assert.False(evt.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task Streaming_SyncSnapshotIsApplied()
    {
        // The poll serves only bool-flag; sse-flag exists solely in the stream's `sync`
        // snapshot, so reading it true proves the SSE snapshot path parsed and applied.
        var polled = """
            {"environment":"test","version":1,"segments":[],"flags":[
              {"key":"bool-flag","version":1,"type":"Boolean","enabled":true,
               "variations":[{"key":"on","value":true},{"key":"off","value":false}],
               "rules":[],"fallthrough":{"type":"Fixed","variation":"off"},"offVariation":"off"}]}
            """;
        var snapshot = """{"environment":"test","version":2,"segments":[],"flags":[{"key":"sse-flag","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}]}""";

        await using var server = new StubFlagServer(polled, syncSnapshot: snapshot);
        using var client = server.CreateClient(streaming: true);
        await client.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var ctx = new EvaluationContext { UserId = "user-1" };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !client.BoolVariation("sse-flag", ctx, false))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var detail = client.VariationDetail("sse-flag", ctx, false);
        Assert.True(detail.Value, $"sync snapshot never applied (reason {detail.Reason}: {detail.ErrorMessage})");
        Assert.Equal(EvaluationReason.Fallthrough, detail.Reason);
        // A full snapshot is a REPLACE: the polled-only flag is gone.
        Assert.Equal(EvaluationReason.FlagNotFound, client.VariationDetail("bool-flag", ctx, false).Reason);
    }

    [Fact]
    public async Task SnapshotStore_PersistsAndRestoresWithoutReflection()
    {
        var store = new RecordingStore();
        string sdkKey;
        string baseUrl;
        await using (var server = new StubFlagServer(PolledFlags))
        {
            sdkKey = server.SdkKey;
            baseUrl = server.BaseUrl;
            using var live = server.CreateClient(streaming: false, snapshotStore: store);
            await live.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await store.WaitForPutAsync();
            Assert.Equal(StartupSource.Live, live.StartupInfo.Source);
        }

        // The first client was disposed at the end of the block above, which shut its core down.
        // The same key and base URL with nothing listening: the client starts from the stored text.
        using var restored = FeatureflipClient.Get(sdkKey, new FeatureFlagOptions
        {
            BaseUrl = baseUrl,
            Streaming = false,
            WaitForInitialization = true,
            InitTimeout = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMinutes(10),
            SnapshotStore = store
        });

        Assert.Equal(StartupSource.Snapshot, restored.StartupInfo.Source);
        var ctx = new EvaluationContext { UserId = "user-1" }.Set("plan", "pro");
        Assert.True(restored.BoolVariation("bool-flag", ctx, false));
        Assert.Equal("hello", restored.StringVariation("string-flag", ctx, "default"));
        var element = restored.VariationDetail("json-flag", ctx, default(JsonElement));
        Assert.Equal("blue", element.Value.GetProperty("color").GetString());
    }

    private sealed class RecordingStore : ISnapshotStore
    {
        private readonly TaskCompletionSource<bool> _put = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string? _text;

        public Task WaitForPutAsync() => _put.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_text);

        public Task PutAsync(string text, CancellationToken cancellationToken)
        {
            _text = text;
            _put.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Flushes until the server has captured an event body. A flush that hits a retryable
    /// failure (e.g. the request timeout on a loaded runner) re-queues the batch and returns
    /// normally, so one flush is not proof of delivery.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> FlushUntilEventsAsync(IFeatureflipClient client, StubFlagServer server)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            await client.FlushAsync();
            var events = server.CapturedEvents();
            if (events.Count > 0 || DateTime.UtcNow >= deadline) return events;
            await Task.Delay(100);
        }
    }

    public sealed class Shape
    {
        public string? Color { get; set; }
        public int Size { get; set; }
    }
}
