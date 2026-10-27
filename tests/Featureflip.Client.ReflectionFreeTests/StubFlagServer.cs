using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Featureflip.Client.ReflectionFreeTests;

/// <summary>
/// Loopback stand-in for the Evaluation API: serves a fixed <c>GET /v1/sdk/flags</c> payload,
/// optionally a <c>sync</c> frame on <c>GET /v1/sdk/stream</c> (then holds the stream open),
/// and captures <c>POST /v1/sdk/events</c> bodies.
/// </summary>
/// <remarks>
/// Adapted from <c>EventCaptureServer</c> in Featureflip.Client.Tests, which cannot be shared
/// by reference: that project runs with reflection enabled and this one must not. Follows the
/// <c>LoopbackServer</c> teardown rules from there — close the listener rather than stopping
/// it, and never join the loop without a bound — because a pending
/// <see cref="HttpListener.GetContextAsync"/> once kept a test host alive until the CI job's
/// 6-hour limit.
/// </remarks>
internal sealed class StubFlagServer : IAsyncDisposable
{
    private static int _keySeq;
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly string _flagsJson;
    private readonly string? _syncSnapshot;
    private readonly string _sdkKey = "reflection-free-" + Interlocked.Increment(ref _keySeq) + "-" + Guid.NewGuid().ToString("N");
    private readonly List<string> _bodies = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public StubFlagServer(string flagsJson, string? syncSnapshot = null)
    {
        _flagsJson = flagsJson;
        _syncSnapshot = syncSnapshot;
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var candidate = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(candidate);
            try
            {
                listener.Start();
                _listener = listener;
                _baseUrl = candidate;
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }

        _loop = Task.Run(LoopAsync);
    }

    public IFeatureflipClient CreateClient(bool streaming) => FeatureflipClient.Get(_sdkKey, new FeatureFlagOptions
    {
        BaseUrl = _baseUrl,
        Streaming = streaming,
        // Background init: the path whose wait used to hang forever (#4301).
        WaitForInitialization = false,
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
                {
                    result.Add(e.Clone());
                }
            }
        }

        return result;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            // Each request on its own task: the stream request holds its connection open.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path.EndsWith("/v1/sdk/events", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (_bodies) _bodies.Add(body);
                ctx.Response.StatusCode = 200;
            }
            else if (path.EndsWith("/v1/sdk/stream", StringComparison.Ordinal))
            {
                if (_syncSnapshot == null)
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                var frame = Encoding.UTF8.GetBytes($"event: sync\ndata: {_syncSnapshot}\n\n");
                await ctx.Response.OutputStream.WriteAsync(frame, 0, frame.Length, _cts.Token);
                await ctx.Response.OutputStream.FlushAsync(_cts.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
            }
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var payload = Encoding.UTF8.GetBytes(_flagsJson);
                await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length, _cts.Token);
            }
        }
        catch
        {
            // Teardown (cancellation, listener closed) or a client that went away.
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(10)));
    }
}
