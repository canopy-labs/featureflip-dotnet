using System.Text.Json;
using System.Text.Json.Nodes;
using Featureflip.Client;
using Featureflip.Client.ReflectionFreeTests;

// Drives the shipped client the way a customer's NativeAOT app would, against a loopback stub
// of the Evaluation API. #4301 failed SILENTLY: every read served its default and every event
// was dropped, with nothing logged. So every check here compares a value the default could not
// produce, and the process exits 1 on any failure (or 2 on an unexpected exception).

const string RuleId = "7f3c2b1a-0d4e-4c5b-9a8f-1e2d3c4b5a69";

// Same payload as ReflectionFreeClientTests: one flag per type, with `prerequisites` and
// `segmentKey` deliberately absent so their initializers must survive deserialization.
const string PolledFlags = """
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

const string SyncSnapshot = """{"environment":"test","version":2,"segments":[],"flags":[{"key":"sse-flag","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}]}""";

var failures = new List<string>();

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures.Add(what);
}

try
{
    Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                      $"dynamic code {(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "supported" : "NOT supported")}");
    // A JIT run proves nothing about AOT; refuse to report green from one.
    Check(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, "running as native code (no dynamic code)");
    Check(!JsonSerializer.IsReflectionEnabledByDefault, "reflection-based System.Text.Json is disabled");

    await PollingAsync();
    await StreamingAsync();
    await SnapshotAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  unexpected exception: {ex}");
    return 2;
}

if (failures.Count > 0)
{
    Console.WriteLine($"{failures.Count} check(s) failed.");
    return 1;
}

Console.WriteLine("All checks passed.");
return 0;

async Task PollingAsync()
{
    await using var server = new StubFlagServer(PolledFlags);
    using var client = server.CreateClient(streaming: false);

    await client.WaitForInitializationAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(client.IsInitialized, "polling: client initialized");

    var ctx = new EvaluationContext { UserId = "user-1" }.Set("plan", "pro");

    var boolDetail = client.VariationDetail("bool-flag", ctx, false);
    Check(boolDetail.Value && boolDetail.Reason == EvaluationReason.RuleMatch,
        $"bool: rule match serves true (got {boolDetail.Value}, {boolDetail.Reason} {boolDetail.ErrorMessage})");

    var str = client.StringVariation("string-flag", ctx, "default");
    Check(str == "hello", $"string: serves \"hello\" (got \"{str}\")");

    var i = client.IntVariation("number-flag", ctx, -1);
    Check(i == 42, $"number: int serves 42 (got {i})");
    var d = client.DoubleVariation("number-flag", ctx, -1);
    Check(d == 42.0, $"number: double serves 42 (got {d})");
    var l = client.Variation("number-flag", ctx, -1L);
    Check(l == 42L, $"number: long serves 42 (got {l})");
    var m = client.Variation("number-flag", ctx, -1m);
    Check(m == 42m, $"number: decimal serves 42 (got {m})");

    var element = client.VariationDetail("json-flag", ctx, default(JsonElement));
    Check(element.Reason == EvaluationReason.Fallthrough
          && element.Value.ValueKind == JsonValueKind.Object
          && element.Value.GetProperty("color").GetString() == "blue",
        $"json: JsonElement serves the object (got {element.Reason} {element.ErrorMessage})");

    var node = client.JsonVariation<JsonNode?>("json-flag", ctx, null);
    Check(node?["size"]?.GetValue<int>() == 3, $"json: JsonNode serves size 3 (got {node?.ToJsonString() ?? "null"})");

    // Evaluation events are the SDK's only event type. A flush that hits a retryable failure
    // re-queues the batch and returns normally, so one flush is not proof of delivery.
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    IReadOnlyList<JsonElement> events;
    while (true)
    {
        await client.FlushAsync();
        events = server.CapturedEvents();
        if (events.Count > 0 || DateTime.UtcNow >= deadline) break;
        await Task.Delay(100);
    }

    var boolEvent = events.FirstOrDefault(e => e.GetProperty("flagKey").GetString() == "bool-flag");
    Check(boolEvent.ValueKind == JsonValueKind.Object, $"events: stub received the flushed batch ({events.Count} event(s))");
    if (boolEvent.ValueKind == JsonValueKind.Object)
    {
        Check(boolEvent.GetProperty("type").GetString() == "Evaluation"
              && boolEvent.GetProperty("userId").GetString() == "user-1"
              && boolEvent.GetProperty("variation").GetString() == "on"
              && boolEvent.GetProperty("reason").GetString() == "rule-match"
              && boolEvent.GetProperty("ruleId").GetString() == RuleId,
            $"events: wire fields intact ({boolEvent.GetRawText()})");
    }
}

async Task StreamingAsync()
{
    // The poll serves only the flags above; sse-flag exists solely in the stream's `sync`
    // snapshot, so reading it true proves the SSE path parsed and applied it.
    await using var server = new StubFlagServer(PolledFlags, syncSnapshot: SyncSnapshot);
    using var client = server.CreateClient(streaming: true);
    await client.WaitForInitializationAsync().WaitAsync(TimeSpan.FromSeconds(10));

    var ctx = new EvaluationContext { UserId = "user-1" };
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (DateTime.UtcNow < deadline && !client.BoolVariation("sse-flag", ctx, false))
    {
        await Task.Delay(50);
    }

    var detail = client.VariationDetail("sse-flag", ctx, false);
    Check(detail.Value && detail.Reason == EvaluationReason.Fallthrough,
        $"streaming: sync snapshot applied (got {detail.Value}, {detail.Reason} {detail.ErrorMessage})");
}

async Task SnapshotAsync()
{
    // Client A persists through a store; the server then goes away and client B starts from the
    // stored text. Exercises the envelope codec, the wire serializer and the file store natively.
    var path = Path.Combine(Path.GetTempPath(), "ff-aot-snapshot-" + Guid.NewGuid().ToString("N"));
    string sdkKey;
    string baseUrl;
    try
    {
        await using (var server = new StubFlagServer(PolledFlags))
        {
            sdkKey = server.SdkKey;
            baseUrl = server.BaseUrl;
            using var live = server.CreateClient(streaming: false, snapshotStore: new FileSnapshotStore(path));
            await live.WaitForInitializationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !(File.Exists(path) && new FileInfo(path).Length > 0))
            {
                await Task.Delay(50);
            }
        }

        using var restored = FeatureflipClient.Get(sdkKey, new FeatureFlagOptions
        {
            BaseUrl = baseUrl,
            Streaming = false,
            WaitForInitialization = true,
            InitTimeout = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMinutes(10),
            SnapshotStore = new FileSnapshotStore(path)
        });

        var ctx = new EvaluationContext { UserId = "user-1" }.Set("plan", "pro");
        Check(restored.StartupInfo.Source == StartupSource.Snapshot, $"snapshot: client started from the stored snapshot (got {restored.StartupInfo})");
        Check(restored.StringVariation("string-flag", ctx, "default") == "hello", "snapshot: stored flag value is served");
    }
    finally
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
