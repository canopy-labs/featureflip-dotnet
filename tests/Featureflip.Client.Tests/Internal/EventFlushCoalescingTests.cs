using System.Net;
using System.Net.Sockets;
using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Regression guard for #2477: at most one drain loop may run at a time.
/// </summary>
/// <remarks>
/// <para>
/// #2456 added a backoff gate plus an in-flight latch, but the latch guarded only the
/// batch-size trigger. Nothing stopped the periodic flush loop, an explicit
/// <c>FlushAsync</c> and a size-triggered flush from entering the drain together — two
/// request streams against the endpoint the gate exists to protect, and a success in one
/// clearing the gate a failure in the other had just armed.
/// </para>
/// <para>
/// The only externally visible evidence of a second drain is a second request arriving
/// while the first is still unanswered, so the listener below parks its first event POST
/// and records the greatest number ever in flight at once.
/// </para>
/// </remarks>
public class EventFlushCoalescingTests
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

    private const string EmptyFlagsJson =
        """{"environment":"test","version":1,"flags":[],"segments":[]}""";

    /// <summary>
    /// Batch size 1 so each tracked event needs its own round-trip, leaving plenty of room
    /// for a second loop to interleave if one is allowed to start. The flush interval is
    /// long enough that FlushLoopAsync never fires on its own.
    /// </summary>
    private static FeatureFlagOptions CoalescingOptions(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        Streaming = false,
        WaitForInitialization = false,
        StartOffline = true,
        InitTimeout = TimeSpan.FromSeconds(2),
        FlushInterval = TimeSpan.FromMinutes(10),
        FlushBatchSize = 1
    };

    private static void TrackOne(SharedFeatureflipCore core, string flagKey) =>
        core.TrackEvaluation(flagKey, new EvaluationContext { UserId = "u1" },
            new EvaluationDetail<bool>(true, EvaluationReason.Fallthrough, null, null, "on"));

    [Fact]
    public async Task FlushAsync_WhenAnotherDrainIsRunning_WaitsForItInsteadOfStartingASecond()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var inFlight = 0;
        var peak = 0;
        var parked = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    // Handled off the accept loop, or parking the first request would stop
                    // the listener from ever seeing a second one — which is the very thing
                    // this test has to be able to observe.
                    _ = Task.Run(async () =>
                    {
                        var depth = Interlocked.Increment(ref inFlight);
                        InterlockedMax(ref peak, depth);

                        // Only the FIRST request is parked; re-parking a later one would
                        // wait on a gate nothing completes again.
                        if (Interlocked.Exchange(ref parked, 1) == 0)
                        {
                            firstArrived.TrySetResult(true);
                            await gate.Task;
                        }

                        Interlocked.Decrement(ref inFlight);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.Close();
                    });
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    var payload = Encoding.UTF8.GetBytes(EmptyFlagsJson);
                    await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length, testTimeout.Token);
                    ctx.Response.Close();
                }
            }
        });

        try
        {
            using var core = new SharedFeatureflipCore("sdk-key", CoalescingOptions(baseUrl), NullLogger.Instance);
            for (var i = 0; i < 6; i++) TrackOne(core, $"flag-{i}");

            var first = core.FlushAsync(testTimeout.Token);
            // The first request is parked in the handler, so the drain loop is provably
            // mid-flight and anything arriving next came from a second one.
            await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var released = 0;
            var second = Task.Run(async () =>
            {
                await core.FlushAsync(testTimeout.Token);
                return Volatile.Read(ref released) == 1;
            });

            // Room for the second caller to misbehave: uncoalesced it drains a batch and
            // posts it, which the peak counter catches.
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            Volatile.Write(ref released, 1);
            gate.TrySetResult(true);

            var secondSawRelease = await second.WaitAsync(TimeSpan.FromSeconds(10));
            await first.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, Volatile.Read(ref peak));
            // A caller that awaited FlushAsync is asking for its events to be sent, so it
            // waits for the drain rather than returning early. Matches the js/node SDKs.
            Assert.True(secondSawRelease,
                "the second FlushAsync returned before the in-flight drain finished");
        }
        finally
        {
            testTimeout.Cancel();
            gate.TrySetResult(true);
            listener.Close();
            try { await serverTask; } catch { /* listener torn down */ }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }
}
