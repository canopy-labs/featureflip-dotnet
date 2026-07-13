using System.Net;
using System.Net.Sockets;
using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Recovery regression guard against the LaunchDarkly failure mode: a running
/// SDK must reconnect after a stream drop with no operator intervention and
/// re-sync full state (including flags deleted during the drop) via the
/// connect-time `sync` snapshot.
/// </summary>
public class SharedFeatureflipCoreRecoveryTests
{
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static HttpListener StartListener(out string baseUrl)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = $"http://127.0.0.1:{GetFreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(candidate);
            try { listener.Start(); baseUrl = candidate; return listener; }
            catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
        }
    }

    // Full-snapshot JSON (same shape as GET /v1/sdk/flags), single line.
    private static string AllFlagsJson(string flagKey) =>
        $$"""{"environment":"test","version":1,"flags":[{"key":"{{flagKey}}","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}],"segments":[]}""";

    private static async Task WriteJsonAsync(HttpListenerContext ctx, string json, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length, ct);
        ctx.Response.Close();
    }

    private static async Task WriteSyncEventAsync(HttpListenerContext ctx, string flagKey, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        var body = Encoding.UTF8.GetBytes($"event: sync\ndata: {AllFlagsJson(flagKey)}\n\n");
        await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length, ct);
        await ctx.Response.OutputStream.FlushAsync(ct);
    }

    [Fact]
    public async Task StreamDrop_Reconnects_AndResyncsFullState()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Server loop: (1) initial /v1/sdk/flags with flag-a so init completes fast;
        // (2) stream #1 sends a `sync` (flag-a) then DROPS; (3) stream #2 — after the
        // SDK reconnects with no intervention — sends a NEW `sync` (flag-b only, so
        // flag-a must be dropped) and holds the connection open.
        var serverTask = Task.Run(async () =>
        {
            var streamConnects = 0;
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/flags"))
                {
                    await WriteJsonAsync(ctx, AllFlagsJson("flag-a"), testTimeout.Token);
                }
                else if (path.EndsWith("/v1/sdk/stream"))
                {
                    streamConnects++;
                    if (streamConnects == 1)
                    {
                        await WriteSyncEventAsync(ctx, "flag-a", testTimeout.Token);
                        ctx.Response.Close(); // drop the stream -> the SDK must reconnect
                    }
                    else
                    {
                        await WriteSyncEventAsync(ctx, "flag-b", testTimeout.Token);
                        // Hold the stream open until the test tears the listener down.
                        await Task.Delay(Timeout.InfiniteTimeSpan, testTimeout.Token);
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        });

        try
        {
            var options = new FeatureFlagOptions
            {
                BaseUrl = baseUrl,
                Streaming = true,
                WaitForInitialization = false,
                StartOffline = true,
                InitTimeout = TimeSpan.FromSeconds(2)
            };
            using var core = new SharedFeatureflipCore("sdk-key", options, NullLogger.Instance);
            var context = new EvaluationContext { UserId = "u1" };

            // After the reconnect (base backoff ~3s), the second snapshot must land:
            // flag-b present, flag-a dropped (full replace) — no manual restart.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (core.Evaluate("flag-b", context, false).Value) break;
                await Task.Delay(100, testTimeout.Token);
            }

            Assert.True(core.Evaluate("flag-b", context, false).Value,
                "SDK should have reconnected and re-synced flag-b with no restart");
            Assert.Equal(EvaluationReason.FlagNotFound, core.Evaluate("flag-a", context, false).Reason);
        }
        finally
        {
            testTimeout.Cancel();
            listener.Stop();
            try { await serverTask; } catch { /* cancellation / listener-stop expected */ }
        }
    }
}
