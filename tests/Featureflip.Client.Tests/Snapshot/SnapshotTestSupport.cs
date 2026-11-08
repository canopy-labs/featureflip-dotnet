using System.Net;
using System.Text;
using Featureflip.Client.Internal;
using Featureflip.Client.Tests.TestSupport;

namespace Featureflip.Client.Tests.Snapshot;

/// <summary>In-memory <see cref="ISnapshotStore"/> that records calls and can be gated or made to fail.</summary>
internal sealed class MemoryStore : ISnapshotStore
{
    private readonly object _gate = new();
    public string? Text { get; set; }
    public List<string> Puts { get; } = new();
    public int Gets;
    public Exception? GetThrows { get; set; }
    public Exception? PutThrows { get; set; }
    public bool IgnoreCancellationOnGet { get; set; }

    /// <summary>Set when a cooperative <c>GetAsync</c> observed its token being cancelled.</summary>
    public volatile bool GetWasCancelled;

    /// <summary>When set, <c>GetAsync</c> awaits it (ignoring the token if configured).</summary>
    public Task? GetGate { get; set; }

    /// <summary>When set, each <c>PutAsync</c> awaits the next gate it dequeues.</summary>
    public Queue<TaskCompletionSource<bool>> PutGates { get; } = new();

    public int PutCount { get { lock (_gate) return Puts.Count; } }

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Gets);
        if (GetThrows != null) throw GetThrows;
        if (GetGate != null)
        {
            if (IgnoreCancellationOnGet) await GetGate.ConfigureAwait(false);
            else
            {
                try
                {
                    await GetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    GetWasCancelled = true;
                    throw;
                }
            }
        }

        return Text;
    }

    public Task PutAsync(string text, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Puts.Add(text);
            Text = text;
        }

        if (PutThrows != null) throw PutThrows;
        TaskCompletionSource<bool>? gate = null;
        lock (_gate)
        {
            if (PutGates.Count > 0) gate = PutGates.Dequeue();
        }

        return gate?.Task ?? Task.CompletedTask;
    }
}

/// <summary>Scripted transport: each request is answered by <see cref="Respond"/>.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

    public int FlagRequests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/v1/sdk/flags", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref FlagRequests);
        }

        return Respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public void Serve(string json) => Respond = (_, _) => Task.FromResult(Json(json));

    public void Fail(HttpStatusCode code = HttpStatusCode.ServiceUnavailable)
        => Respond = (_, _) => Task.FromResult(Status(code));

    /// <summary>Never answers; ends only when the request token is cancelled.</summary>
    public void Hang() => Respond = async (_, ct) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return Status(HttpStatusCode.OK);
    };
}

internal static class SnapshotFixtures
{
    public const string BaseUrl = "https://eval.example.test";

    /// <summary>One boolean flag served <c>on</c> (true) or <c>off</c>, in the real wire shape.</summary>
    public static string FlagsJson(string key = "flag-a", bool on = true, string environment = "test", int version = 1)
        => $$"""{"environment":"{{environment}}","version":{{version}},"flags":[{"key":"{{key}}","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"{{(on ? "on" : "off")}}"},"offVariation":"off"}],"segments":[]}""";

    public static FeatureFlagOptions Options(ISnapshotStore? store, bool sync = true, TimeSpan? initTimeout = null)
        => new()
        {
            BaseUrl = BaseUrl,
            WaitForInitialization = sync,
            InitTimeout = initTimeout ?? TimeSpan.FromSeconds(2),
            SnapshotStore = store,
            Streaming = false
        };

    public static SharedFeatureflipCore NewCore(
        FeatureFlagOptions options,
        ScriptedHandler handler,
        LevelCapturingLogger? logger = null,
        string sdkKey = "sdk-key",
        Func<long>? wallMs = null,
        Func<long>? monotonicMs = null,
        Action<Action>? startWorker = null)
        => new(sdkKey, options, logger ?? new LevelCapturingLogger(), new SharedFeatureflipCore.CoreTestHooks
        {
            Handler = handler,
            StartDataSources = false,
            WallMs = wallMs,
            MonotonicMs = monotonicMs,
            // Inline by default: with a store that completes synchronously the whole write has
            // finished before Persist returns, so tests need no waiting.
            StartWorker = startWorker ?? (work => work())
        });

    public static string Envelope(string sdkKey, long fetchedAtMs, string? dataJson = null, string baseUrl = BaseUrl)
        => SnapshotCodec.EncodeEnvelope(SnapshotCodec.ComputeKeyHash(baseUrl, sdkKey), fetchedAtMs, dataJson ?? FlagsJson());

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    public static bool Warned(LevelCapturingLogger logger, string contains)
        => logger.Snapshot().Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains(contains, StringComparison.Ordinal));
}
