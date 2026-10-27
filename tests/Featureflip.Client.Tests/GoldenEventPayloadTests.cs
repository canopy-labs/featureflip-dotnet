using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Featureflip.Client.Internal;
using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>
/// Runner for the <c>kind == "evaluation"</c> vectors of the shared <c>eventPayloadVectors</c>
/// class (#4268). C# has no identify/track by design (#2397), so those kinds are skipped
/// explicitly. Drives a REAL client (a for-testing stub bypasses analytics) against a local
/// HTTP listener and inspects the event AS SERIALIZED in the POST /v1/sdk/events body.
/// </summary>
// Same collection as FeatureflipClientFactoryTests: this class builds real clients through
// FeatureflipClient.Get, whose core map is process-wide. Run in parallel, that class's
// ResetForTesting() force-shut these cores mid-test (the evaluation was then dropped and the
// flush found no events), and these cores inflated its DebugLiveCoreCount assertion.
[Collection("FactoryStaticState")]
public class GoldenEventPayloadTests
{
    private static readonly JsonElement Block = LoadBlock();

    private static JsonElement LoadBlock()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("eventPayloadVectors").Clone();
    }

    private static IEnumerable<JsonElement> VectorList() =>
        Block.ValueKind == JsonValueKind.Array
            ? Block.EnumerateArray()
            : Block.GetProperty("vectors").EnumerateArray();

    public static IEnumerable<object[]> EvaluationVectors() =>
        VectorList()
            .Where(v => v.GetProperty("kind").GetString() == "evaluation")
            .Select(v => new object[] { v.GetProperty("id").GetString()!, v });

    [Theory]
    [MemberData(nameof(EvaluationVectors))]
    public async Task EvaluationEventPayload(string id, JsonElement vector)
    {
        var flagsJson = "{\"environment\":\"test\",\"version\":1,\"flags\":"
            + vector.GetProperty("flags").GetRawText()
            + ",\"segments\":" + vector.GetProperty("segments").GetRawText() + "}";

        await using var server = new EventCaptureServer(flagsJson);
        using var client = server.CreateClient();

        var ctx = new EvaluationContext();
        foreach (var prop in vector.GetProperty("context").EnumerateObject())
        {
            if (prop.Name == "user_id") ctx.UserId = prop.Value.GetString();
            else ctx.Set(prop.Name, ToClr(prop.Value));
        }

        client.BoolVariation(vector.GetProperty("flagKey").GetString()!, ctx, false);
        await client.FlushAsync();

        var events = server.CapturedEvents();
        var evt = Assert.Single(events);

        var expect = vector.GetProperty("expect");
        var expected = expect.EnumerateObject().Select(p => p.Name).Append("timestamp").OrderBy(n => n, StringComparer.Ordinal);
        var actual = evt.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.True(expected.SequenceEqual(actual),
            $"[{id}] key set mismatch: expected [{string.Join(",", expected)}] got [{string.Join(",", actual)}] in {evt.GetRawText()}");

        foreach (var p in expect.EnumerateObject())
        {
            Assert.True(JsonDeepEquals(p.Value, evt.GetProperty(p.Name)),
                $"[{id}] field {p.Name}: expected {p.Value.GetRawText()} got {evt.GetProperty(p.Name).GetRawText()}");
        }

        var ts = evt.GetProperty("timestamp").GetString();
        Assert.True(DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && parsed.Offset == TimeSpan.Zero, $"[{id}] timestamp not an ISO-8601 UTC instant: {ts}");

    }

    [Fact]
    public void RunnerCoversAtLeastSixEvaluationVectors()
    {
        Assert.True(EvaluationVectors().Count() >= 6);
    }

    private static object ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        _ => e.GetRawText()
    };

    private static bool JsonDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var ao = a.EnumerateObject().ToList();
                if (ao.Count != b.EnumerateObject().Count()) return false;
                return ao.All(p => b.TryGetProperty(p.Name, out var bv) && JsonDeepEquals(p.Value, bv));
            case JsonValueKind.Array:
                var aa = a.EnumerateArray().ToList();
                var ba = b.EnumerateArray().ToList();
                return aa.Count == ba.Count && aa.Zip(ba, JsonDeepEquals).All(x => x);
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble();
            default:
                return true;
        }
    }
}

/// <summary>
/// Local HTTP server serving a fixed /v1/sdk/flags payload and capturing /v1/sdk/events bodies.
/// </summary>
internal sealed class EventCaptureServer : IAsyncDisposable
{
    private static int _keySeq;
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly string _flagsJson;
    private readonly string _sdkKey = "event-capture-" + Interlocked.Increment(ref _keySeq) + "-" + Guid.NewGuid().ToString("N");
    private readonly List<string> _bodies = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public EventCaptureServer(string flagsJson)
    {
        _flagsJson = flagsJson;
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var candidate = $"http://127.0.0.1:{port}/";
            var l = new HttpListener();
            l.Prefixes.Add(candidate);
            try { l.Start(); _listener = l; _baseUrl = candidate; break; }
            catch (HttpListenerException) when (attempt < 10) { l.Close(); }
        }
        _loop = Task.Run(Loop);
    }

    public IFeatureflipClient CreateClient() => FeatureflipClient.Get(_sdkKey, new FeatureFlagOptions
    {
        BaseUrl = _baseUrl,
        Streaming = false,
        WaitForInitialization = true,
        InitTimeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMinutes(10),
        FlushInterval = TimeSpan.FromMinutes(10),
        FlushBatchSize = 1000
    });

    public IReadOnlyList<JsonElement> CapturedEvents()
    {
        var result = new List<JsonElement>();
        lock (_bodies)
        {
            foreach (var body in _bodies)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
                    result.Add(e.Clone());
            }
        }
        return result;
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (_bodies) _bodies.Add(body);
                    ctx.Response.StatusCode = 200;
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    var payload = Encoding.UTF8.GetBytes(_flagsJson);
                    await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
                }
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        try { await _loop; } catch { }
    }
}
