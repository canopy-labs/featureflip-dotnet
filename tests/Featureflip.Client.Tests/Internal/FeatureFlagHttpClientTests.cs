using System.Net;
using System.Net.Sockets;
using System.Text;
using Featureflip.Client.Internal;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Regression tests for the HTTP client's timeout handling.
///
/// <para>
/// The SSE streaming connection is long-lived, but <see cref="System.Net.Http.HttpClient.Timeout"/>
/// is a <em>total</em> request timeout in .NET — so reusing the one-shot client's finite
/// <c>ReadTimeout</c> for the stream aborts it with a <see cref="TaskCanceledException"/>
/// (even bounding its time-to-first-byte) and real-time flag updates never sustain (#1526).
/// </para>
/// </summary>
public class FeatureFlagHttpClientTests
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
        // There is an unavoidable TOCTOU window between probing a free port and binding the
        // HttpListener to it; on a busy/shared CI runner the port can be taken in between.
        // Retry with a fresh port to keep the test from flaking.
        for (var attempt = 0; ; attempt++)
        {
            var candidate = $"http://127.0.0.1:{GetFreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(candidate);
            try
            {
                listener.Start();
                baseUrl = candidate;
                return listener;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }
    }

    [Fact]
    public async Task GetStreamAsync_SlowToConnect_IsNotAbortedByReadTimeout()
    {
        var listener = StartListener(out var baseUrl);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readTimeout = TimeSpan.FromMilliseconds(300);
        var headerDelay = TimeSpan.FromMilliseconds(900); // > readTimeout

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();

            // Delay sending ANY response (headers) past the one-shot ReadTimeout. The SSE
            // stream is long-lived, so its time-to-first-byte must not be bounded by the
            // finite ReadTimeout — only by ConnectTimeout/the shutdown CancellationToken.
            await Task.Delay(headerDelay, testTimeout.Token);

            var resp = ctx.Response;
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream";
            var body = Encoding.UTF8.GetBytes("data: hello\n\n");
            await resp.OutputStream.WriteAsync(body, 0, body.Length, testTimeout.Token);
            await resp.OutputStream.FlushAsync(testTimeout.Token);
            resp.Close();
        });

        try
        {
            var options = new FeatureFlagOptions { BaseUrl = baseUrl, ReadTimeout = readTimeout };
            using var client = new FeatureFlagHttpClient("sdk-key", options);

            // With the bug, the shared client's finite ReadTimeout (300ms) aborts the
            // streaming SendAsync with TaskCanceledException before the server responds.
            // With the fix the dedicated streaming client has an infinite timeout, so the
            // connection completes (~900ms) and the stream is returned and readable.
            using var stream = await client.GetStreamAsync(testTimeout.Token);

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, testTimeout.Token);

            Assert.True(read > 0);
            Assert.Contains("hello", Encoding.UTF8.GetString(buffer, 0, read));

            // Surface any server-side fault on the happy path.
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveConnectTimeout_DoesNotThrow(int connectTimeoutSeconds)
    {
        // ConnectTimeout was previously unused; the streaming client is its first consumer.
        // A non-positive value must not crash construction (SocketsHttpHandler.ConnectTimeout
        // rejects it), so the streaming client treats it as an unbounded connect.
        var options = new FeatureFlagOptions
        {
            BaseUrl = "http://localhost:1/",
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds)
        };

        using var client = new FeatureFlagHttpClient("sdk-key", options);

        Assert.NotNull(client);
    }

    [Fact]
    public async Task GetFlagsAsync_StillRespectsReadTimeout()
    {
        var listener = StartListener(out var baseUrl);
        using var serverDone = new CancellationTokenSource();
        var readTimeout = TimeSpan.FromMilliseconds(300);

        var serverTask = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                // Stall the one-shot response well beyond ReadTimeout so its total timeout
                // fires — the one-shot client must stay bounded by ReadTimeout.
                await Task.Delay(TimeSpan.FromSeconds(10), serverDone.Token);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (OperationCanceledException) { /* test finished */ }
            catch (HttpListenerException) { /* listener stopped */ }
            catch (ObjectDisposedException) { /* listener disposed */ }
        });

        try
        {
            var options = new FeatureFlagOptions { BaseUrl = baseUrl, ReadTimeout = readTimeout };
            using var client = new FeatureFlagHttpClient("sdk-key", options);

            await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetFlagsAsync());
        }
        finally
        {
            serverDone.Cancel();
            await serverTask; // expected exceptions are swallowed inside; real faults surface
            listener.Stop();
        }
    }
}
