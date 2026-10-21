using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// The SDK reports <c>featureflip-csharp/&lt;version&gt;</c> in the User-Agent of every request
/// (#4139). Before this it sent none, so its prod traffic arrived as <c>nil</c> and a customer
/// on .NET could not be told apart from management-api's own dogfood client.
/// </summary>
/// <remarks>
/// Asserted on the wire, through the shipped <see cref="FeatureFlagHttpClient"/>, for all three
/// request paths. The poll and event clients share one <see cref="HttpClient"/> but the stream
/// has its own, so a header added to only one of them would leave stream connects anonymous.
/// </remarks>
public class SdkIdentityTests
{
    private static readonly Regex SemVer = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");

    [Fact]
    public void Version_IsTheAssemblyVersionThePublishWorkflowWrites()
    {
        // publish-csharp-sdk.yml rewrites <Version> from the tag, which the build stamps into
        // AssemblyInformationalVersion. Deriving from that, not a literal, is what stops the
        // UA advertising a stale version across a release line (#2141).
        var informational = typeof(FeatureFlagHttpClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0], SdkIdentity.Version);
        Assert.Matches(SemVer, SdkIdentity.Version);
    }

    [Fact]
    public async Task EveryRequestPath_SendsTheSdkToken()
    {
        var headers = await CaptureUserAgentsAsync(new FeatureFlagOptions());

        var expected = $"featureflip-csharp/{SdkIdentity.Version}";
        Assert.Equal(expected, headers["/v1/sdk/flags"]);
        Assert.Equal(expected, headers["/v1/sdk/events"]);
        Assert.Equal(expected, headers["/v1/sdk/stream"]);
    }

    [Fact]
    public async Task Wrapper_IsAppendedAsASecondProductToken()
    {
        var options = new FeatureFlagOptions { WrapperName = "featureflip-openfeature-dotnet", WrapperVersion = "1.2.3" };

        var headers = await CaptureUserAgentsAsync(options);

        var expected = $"featureflip-csharp/{SdkIdentity.Version} featureflip-openfeature-dotnet/1.2.3";
        Assert.Equal(expected, headers["/v1/sdk/flags"]);
        Assert.Equal(expected, headers["/v1/sdk/events"]);
        Assert.Equal(expected, headers["/v1/sdk/stream"]);
    }

    [Fact]
    public async Task Wrapper_WithoutAVersion_IsSentAsABareName()
    {
        var headers = await CaptureUserAgentsAsync(new FeatureFlagOptions { WrapperName = "my-wrapper" });

        Assert.Equal($"featureflip-csharp/{SdkIdentity.Version} my-wrapper", headers["/v1/sdk/flags"]);
    }

    [Fact]
    public async Task InvalidWrapper_IsDroppedWithAWarning_AndTheSdkTokenStillSent()
    {
        // A space is not a legal product-token character. The wrapper is identity metadata, so
        // a malformed one must never stop the client constructing; it is dropped and said so.
        var logger = new LevelCapturingLogger();
        var options = new FeatureFlagOptions { WrapperName = "bad name", WrapperVersion = "1.0" };

        var headers = await CaptureUserAgentsAsync(options, logger);

        Assert.Equal($"featureflip-csharp/{SdkIdentity.Version}", headers["/v1/sdk/flags"]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("bad name"));
    }

    [Fact]
    public async Task NoWrapper_LogsNothing()
    {
        var logger = new LevelCapturingLogger();

        await CaptureUserAgentsAsync(new FeatureFlagOptions(), logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// Drives one request down each path against a loopback stub and returns the User-Agent
    /// each one arrived with, keyed by path.
    /// </summary>
    private static async Task<Dictionary<string, string?>> CaptureUserAgentsAsync(
        FeatureFlagOptions options, ILogger? logger = null)
    {
        var listener = StartListener(out var baseUrl);
        var seen = new Dictionary<string, string?>();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var ctx = await listener.GetContextAsync();
                lock (seen) seen[ctx.Request.Url!.AbsolutePath] = ctx.Request.UserAgent;

                var resp = ctx.Response;
                resp.StatusCode = 200;
                var (contentType, body) = ctx.Request.Url!.AbsolutePath switch
                {
                    "/v1/sdk/flags" => ("application/json", "{\"environment\":\"e\",\"version\":1,\"flags\":[],\"segments\":[]}"),
                    "/v1/sdk/stream" => ("text/event-stream", "data: hello\n\n"),
                    _ => ("application/json", "{}"),
                };
                resp.ContentType = contentType;
                var bytes = Encoding.UTF8.GetBytes(body);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length, testTimeout.Token);
                resp.Close();
            }
        });

        try
        {
            options.BaseUrl = baseUrl;
            using var client = new FeatureFlagHttpClient("sdk-key", options, logger);

            await client.GetFlagsAsync(testTimeout.Token);
            await client.SendEventsAsync(
                new[] { new SdkEvent { Type = "Custom", FlagKey = "f", UserId = "u", Timestamp = DateTime.UtcNow } },
                testTimeout.Token);
            using (var stream = await client.GetStreamAsync(testTimeout.Token))
            {
                var buffer = new byte[64];
                _ = await stream.ReadAsync(buffer, 0, buffer.Length, testTimeout.Token);
            }

            Assert.True(await LoopbackServer.JoinAsync(serverTask));
            return seen;
        }
        finally
        {
            listener.Close();
            await LoopbackServer.JoinAsync(serverTask);
        }
    }

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
        // Retry the probe-then-bind TOCTOU window, as FeatureFlagHttpClientTests does.
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
}
