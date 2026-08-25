using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Regression guard for #2456: analytics events must survive a transient failure of the
/// events endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The flush path drains the queue <em>before</em> the POST, so until this was fixed a
/// single non-2xx discarded that batch permanently — there was no re-queue and no retry.
/// In production the public edge answers the event flush with a 503 at a low but constant
/// rate, so a steady trickle of evaluation analytics was being lost outright.
/// </para>
/// <para>
/// The distinction under test is between a <em>retryable</em> failure (5xx, 429, transport)
/// which must preserve the batch, and a <em>permanent</em> one (401/403/400) which must
/// drop it — retrying a rejected SDK key forever would pin the queue at its bound and
/// starve every later event.
/// </para>
/// </remarks>
public class EventFlushResilienceTests
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
    /// Options that keep the background loops out of the way: a long flush interval so
    /// FlushLoopAsync never fires on its own, and a batch size far above what the test
    /// enqueues so TrackEvaluation does not auto-flush. Every flush under test is explicit.
    /// </summary>
    private static FeatureFlagOptions QuietOptions(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        Streaming = false,
        WaitForInitialization = false,
        StartOffline = true,
        InitTimeout = TimeSpan.FromSeconds(2),
        FlushInterval = TimeSpan.FromMinutes(10),
        FlushBatchSize = 1000
    };

    private static void TrackOne(SharedFeatureflipCore core, string flagKey) =>
        core.TrackEvaluation(flagKey, new EvaluationContext { UserId = "u1" },
            new EvaluationDetail<bool>(true, EvaluationReason.Fallthrough, null, null, "on"));

    [Fact]
    public async Task Flush_WhenEventsEndpointReturns503_KeepsEventsForTheNextFlush()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventPosts = 0;
        var deliveredBodies = new List<string>();

        // First POST /v1/sdk/events answers 503 (the production edge failure); the second
        // answers 200 and records what finally arrived.
        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    var attempt = Interlocked.Increment(ref eventPosts);
                    if (attempt == 1)
                    {
                        ctx.Response.StatusCode = 503;
                    }
                    else
                    {
                        lock (deliveredBodies) deliveredBodies.Add(body);
                        ctx.Response.StatusCode = 200;
                    }
                    ctx.Response.Close();
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
            using var core = new SharedFeatureflipCore("sdk-key", QuietOptions(baseUrl), NullLogger.Instance);
            TrackOne(core, "flag-a");

            // Flush #1 hits the 503. The batch must NOT be lost.
            await core.FlushAsync(testTimeout.Token);

            // Flush #2 succeeds and must carry the event from flush #1.
            await core.FlushAsync(testTimeout.Token);

            Assert.Equal(2, Volatile.Read(ref eventPosts));
            string delivered;
            lock (deliveredBodies)
            {
                Assert.Single(deliveredBodies);
                delivered = deliveredBodies[0];
            }
            Assert.Contains("flag-a", delivered);
        }
        finally
        {
            testTimeout.Cancel();
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Flush_WhileTheEndpointIsFailing_DoesNotFlushOnEveryTrackedEvaluation()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventPosts = 0;

        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    _ = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    Interlocked.Increment(ref eventPosts);
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
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
            // A batch size of 1 means every tracked evaluation trips the size trigger.
            var options = QuietOptions(baseUrl);
            options.FlushBatchSize = 1;
            using var core = new SharedFeatureflipCore("sdk-key", options, NullLogger.Instance);

            // The re-queue leaves the queue at or above the batch size, so without a
            // backoff on the size trigger each of these would start its own flush and a
            // failing endpoint would get one request per evaluation.
            for (var i = 0; i < 10; i++)
                TrackOne(core, $"flag-{i}");

            // Give any flushes the size trigger did start time to reach the listener.
            await Task.Delay(500, testTimeout.Token);

            Assert.Equal(1, Volatile.Read(ref eventPosts));
        }
        finally
        {
            testTimeout.Cancel();
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Flush_AfterAFailureSettles_StillDoesNotFlushOnEveryTrackedEvaluation()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventPosts = 0;

        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    _ = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    Interlocked.Increment(ref eventPosts);
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
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
            var options = QuietOptions(baseUrl);
            options.FlushBatchSize = 1;
            using var core = new SharedFeatureflipCore("sdk-key", options, NullLogger.Instance);

            // One evaluation, and let its flush fail and fully settle — so the in-flight
            // latch is released and only the backoff gate is left holding the trigger.
            TrackOne(core, "flag-seed");
            await Task.Delay(600, testTimeout.Token);
            Assert.Equal(1, Volatile.Read(ref eventPosts));

            // Events arriving after that: each is spaced far enough apart that the latch
            // cannot be what suppresses them.
            for (var i = 0; i < 10; i++)
            {
                TrackOne(core, $"flag-{i}");
                await Task.Delay(30, testTimeout.Token);
            }
            await Task.Delay(300, testTimeout.Token);

            Assert.Equal(1, Volatile.Read(ref eventPosts));
        }
        finally
        {
            testTimeout.Cancel();
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Flush_SendsOneRequestPerBatch_NotOneForTheWholeQueue()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failSends = true;
        var batchSizes = new List<int>();

        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    if (Volatile.Read(ref failSends))
                    {
                        ctx.Response.StatusCode = 503;
                    }
                    else
                    {
                        lock (batchSizes)
                            batchSizes.Add(System.Text.RegularExpressions.Regex.Matches(body, "\"flagKey\"").Count);
                        ctx.Response.StatusCode = 200;
                    }
                    ctx.Response.Close();
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
            var options = QuietOptions(baseUrl);
            options.FlushBatchSize = 2;
            using var core = new SharedFeatureflipCore("sdk-key", options, NullLogger.Instance);

            // Build a backlog the way an outage does: the first size-triggered flush fails,
            // the batch is re-queued, and the backoff gate then holds the size trigger off
            // while the remaining events pile up behind it.
            for (var i = 0; i < 5; i++)
            {
                TrackOne(core, $"flag-{i}");
                await Task.Delay(40, testTimeout.Token);
            }
            await Task.Delay(300, testTimeout.Token);

            // Endpoint recovers; the backlog must drain in batch-sized requests.
            Volatile.Write(ref failSends, false);
            await core.FlushAsync(testTimeout.Token);

            lock (batchSizes)
            {
                Assert.NotEmpty(batchSizes);
                // A backlog must never go out as one oversized request: the server would
                // reject it with a 413, which is non-retryable, so the path meant to
                // preserve the backlog would be the path that discarded it.
                Assert.All(batchSizes, n =>
                    Assert.True(n <= 2, $"a request carried {n} events, above the batch size of 2"));
                Assert.Equal(5, batchSizes.Sum());
            }
        }
        finally
        {
            testTimeout.Cancel();
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task Flush_WhenEventsEndpointReturns401_DropsEventsWithoutRetrying()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventPosts = 0;

        // Every POST /v1/sdk/events is rejected as unauthorized — a permanent failure.
        var serverTask = Task.Run(async () =>
        {
            while (!testTimeout.Token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                if (path.EndsWith("/v1/sdk/events"))
                {
                    _ = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    Interlocked.Increment(ref eventPosts);
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
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
            using var core = new SharedFeatureflipCore("sdk-key", QuietOptions(baseUrl), NullLogger.Instance);
            TrackOne(core, "flag-a");

            await core.FlushAsync(testTimeout.Token);
            // The batch was rejected for a reason a retry cannot change, so it is gone and
            // the second flush has nothing to send.
            await core.FlushAsync(testTimeout.Token);

            Assert.Equal(1, Volatile.Read(ref eventPosts));
        }
        finally
        {
            testTimeout.Cancel();
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* teardown */ }
        }
    }
}
