using System.Net;
using System.Net.Sockets;
using System.Text;
using Featureflip.Client.Tests.Internal;
using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

/// <summary>The public client end to end: the shared parity scenario and the factory warning.</summary>
[Collection("FactoryStaticState")]
public sealed class SnapshotClientTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "ff-client-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SnapshotClientTests() => FeatureflipClient.ResetForTesting();

    public void Dispose()
    {
        FeatureflipClient.ResetForTesting();
        try { File.Delete(_file); } catch { /* best effort */ }
    }

    private sealed class Capture<T> : ILogger<T>
    {
        public LevelCapturingLogger Inner { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static HttpListener StartServer(out string baseUrl)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var candidate = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(candidate);
            try { listener.Start(); baseUrl = candidate; return listener; }
            catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
        }
    }

    private static Task Serve(HttpListener listener, string json) => Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { return; }

            if (ctx.Request.Url!.AbsolutePath.EndsWith("/v1/sdk/flags", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                var body = Encoding.UTF8.GetBytes(json);
                await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }

            ctx.Response.Close();
        }
    });

    [Fact]
    public async Task Parity_ClientAWritesTheFile_ServerStops_FreshClientBStartsFromTheSnapshot()
    {
        var listener = StartServer(out var baseUrl);
        var serverTask = Serve(listener, FlagsJson("parity-flag"));
        try
        {
            var optionsA = new FeatureFlagOptions
            {
                BaseUrl = baseUrl, Streaming = false, PollInterval = TimeSpan.FromMinutes(10),
                WaitForInitialization = true, InitTimeout = TimeSpan.FromSeconds(5),
                SnapshotStore = new FileSnapshotStore(_file)
            };
            using (var a = FeatureflipClient.Get("parity-key", optionsA))
            {
                Assert.True(a.BoolVariation("parity-flag", new EvaluationContext { UserId = "u" }, false));
                Assert.Equal(StartupSource.Live, a.StartupInfo.Source);
                await WaitUntil(() => File.Exists(_file) && new FileInfo(_file).Length > 0);
            }
        }
        finally
        {
            listener.Close();
            try { await LoopbackServer.JoinAsync(serverTask); } catch { /* listener closed */ }
        }

        FeatureflipClient.ResetForTesting();
        var optionsB = new FeatureFlagOptions
        {
            BaseUrl = baseUrl, Streaming = false, PollInterval = TimeSpan.FromMinutes(10),
            WaitForInitialization = true, InitTimeout = TimeSpan.FromSeconds(1),
            SnapshotStore = new FileSnapshotStore(_file)
        };
        using var b = FeatureflipClient.Get("parity-key", optionsB);

        Assert.True(b.IsInitialized);
        Assert.True(b.BoolVariation("parity-flag", new EvaluationContext { UserId = "u" }, false));
        Assert.Equal(StartupSource.Snapshot, b.StartupInfo.Source);
        Assert.Equal("snapshot", b.StartupInfo.Reason);
    }

    private static FeatureFlagOptions Offline(ISnapshotStore? store, TimeSpan? maxAge = null)
    {
        var options = new FeatureFlagOptions
        {
            BaseUrl = "http://127.0.0.1:1", Streaming = false, StartOffline = true,
            InitTimeout = TimeSpan.FromSeconds(1), SnapshotStore = store
        };
        if (maxAge != null) options.MaxSnapshotAge = maxAge.Value;
        return options;
    }

    private static bool Ignored(Capture<FeatureflipClient> logger)
        => logger.Inner.Snapshot().Any(e => e.Level == LogLevel.Warning && e.Message.Contains("different options", StringComparison.Ordinal));

    [Fact]
    public void Get_WithADifferentStoreInstance_WarnsThatTheOptionIsIgnored()
    {
        var logger = new Capture<FeatureflipClient>();
        using var first = FeatureflipClient.Get("factory-store-a", Offline(new MemoryStore()), logger);
        using var second = FeatureflipClient.Get("factory-store-a", Offline(new MemoryStore()), logger);

        Assert.True(Ignored(logger));
    }

    [Fact]
    public void Get_WithTheSameStoreInstance_StaysSilent()
    {
        var logger = new Capture<FeatureflipClient>();
        var store = new MemoryStore();
        using var first = FeatureflipClient.Get("factory-store-b", Offline(store), logger);
        using var second = FeatureflipClient.Get("factory-store-b", Offline(store), logger);

        Assert.False(Ignored(logger));
    }

    [Fact]
    public void Get_WithADifferentMaxAge_Warns()
    {
        var logger = new Capture<FeatureflipClient>();
        var store = new MemoryStore();
        using var first = FeatureflipClient.Get("factory-age-a", Offline(store, TimeSpan.FromHours(1)), logger);
        using var second = FeatureflipClient.Get("factory-age-a", Offline(store, TimeSpan.FromHours(2)), logger);

        Assert.True(Ignored(logger));
    }

    [Fact]
    public void Get_OmittingTheSnapshotOptions_DoesNotWarn()
    {
        var logger = new Capture<FeatureflipClient>();
        var store = new MemoryStore();
        using var first = FeatureflipClient.Get("factory-omit", Offline(store, TimeSpan.FromHours(1)), logger);
        using var second = FeatureflipClient.Get("factory-omit", Offline(null), logger);

        Assert.False(Ignored(logger));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Get_InvalidMaxAgeOnAReusedCore_ThrowsLikeTheFirstGet(int seconds)
    {
        var logger = new Capture<FeatureflipClient>();
        var store = new MemoryStore();
        Assert.Throws<FeatureFlagInitializationException>(
            () => FeatureflipClient.Get("factory-bad-age-first", Offline(store, TimeSpan.FromSeconds(seconds)), logger));

        using var first = FeatureflipClient.Get("factory-bad-age-reuse", Offline(store, TimeSpan.FromHours(1)), logger);
        Assert.Throws<FeatureFlagInitializationException>(
            () => FeatureflipClient.Get("factory-bad-age-reuse", Offline(store, TimeSpan.FromSeconds(seconds)), logger));
        Assert.False(Ignored(logger));
    }

    [Fact]
    public void Get_InitTimeoutNotPositive_WithAStore_ThrowsOnFirstAndReusedGet()
    {
        var logger = new Capture<FeatureflipClient>();
        var bad = Offline(new MemoryStore());
        bad.InitTimeout = TimeSpan.Zero;
        Assert.Throws<FeatureFlagInitializationException>(() => FeatureflipClient.Get("factory-bad-init-first", bad, logger));

        using var first = FeatureflipClient.Get("factory-bad-init-reuse", Offline(new MemoryStore()), logger);
        bad.InitTimeout = TimeSpan.FromSeconds(-1);
        Assert.Throws<FeatureFlagInitializationException>(() => FeatureflipClient.Get("factory-bad-init-reuse", bad, logger));
    }

    [Fact]
    public void Get_InitTimeoutNotPositive_WithoutAStore_IsNotRejected()
    {
        var options = Offline(null);
        options.InitTimeout = TimeSpan.Zero;
        using var client = FeatureflipClient.Get("factory-zero-init-nostore", options);
        Assert.NotNull(client);
    }

    [Fact]
    public void Get_FirstWithoutAStore_ThenLaterWithOne_Warns()
    {
        var logger = new Capture<FeatureflipClient>();
        using var first = FeatureflipClient.Get("factory-late-store", Offline(null), logger);
        using var second = FeatureflipClient.Get("factory-late-store", Offline(new MemoryStore()), logger);

        Assert.True(Ignored(logger));
    }
}
