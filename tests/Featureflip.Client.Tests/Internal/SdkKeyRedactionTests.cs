using System.Net;
using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// The SDK key is a credential and customers ship SDK logs to aggregators, so it must never
/// reach a log line (#5167, #5166). The redactor wraps the logger once at core construction.
/// </summary>
public class SdkKeyRedactionTests
{
    private const string Sentinel = "sdk_server_SentinelKeyMustNeverAppearInLogs-0123456789";

    /// <summary>Records every entry the way a structured sink would: message, state values, exception text, scopes.</summary>
    private sealed class CaptureLogger : ILogger
    {
        public List<string> Output { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lock (Output) Output.Add("scope:" + RenderState(state));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + "|" + RenderState(state) + "|" + exception;
            lock (Output) Output.Add(text);
        }

        private static string RenderState<TState>(TState state)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
                return string.Join(",", list.Select(kv => kv.Key + "=" + kv.Value));
            return state?.ToString() ?? "";
        }

        public string All { get { lock (Output) return string.Join("\n", Output); } }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    private const string FlagsJson =
        """{"environment":"test","version":1,"flags":[{"key":"f","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}],"segments":[]}""";

    private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // A stack that quotes the request's Authorization header in its error, as some HTTP clients do.
    private static HttpResponseMessage Transport(HttpRequestMessage r) =>
        throw new HttpRequestException(
            "request failed; headers: Authorization: " + string.Join(",", r.Headers.GetValues("Authorization")));

    private static FeatureFlagOptions Options() => new()
    {
        BaseUrl = "http://127.0.0.1:1/",
        Streaming = false,
        WaitForInitialization = false,
        PollInterval = TimeSpan.FromMilliseconds(50),
        FlushInterval = TimeSpan.FromMilliseconds(50),
        InitTimeout = TimeSpan.FromSeconds(3),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = TimeSpan.FromSeconds(2),
    };

    // ---- unit-level: the decorator ----

    [Fact]
    public void Exception_MessageContainingKey_IsRedacted()
    {
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(sink, Sentinel);

        log.LogWarning(new InvalidOperationException("boom Authorization: " + Sentinel), "request failed");

        Assert.DoesNotContain(Sentinel, sink.All);
        Assert.Contains("[redacted]", sink.All);
        Assert.Contains("InvalidOperationException", sink.All);
    }

    [Fact]
    public void MessageAndStructuredFields_AreRedacted()
    {
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(sink, Sentinel);

        log.LogInformation("shutting down {Key} and {Obj}", Sentinel, new Uri("http://h/" + Sentinel));
        log.LogInformation("literal " + Sentinel);

        Assert.DoesNotContain(Sentinel, sink.All);
        Assert.Contains("Key=[redacted]", sink.All);
    }

    [Fact]
    public void Scope_IsRedacted()
    {
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(sink, Sentinel);
        using (log.BeginScope("scope " + Sentinel)) { }
        Assert.DoesNotContain(Sentinel, sink.All);
    }

    [Fact]
    public void ShortKey_IsNotRedacted()
    {
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(sink, "k");
        Assert.Same(sink, log);
        log.LogInformation("skipped a malformed flag");
        Assert.Contains("skipped a malformed flag", sink.All);
    }

    // ---- behavioural sentinel: drive the noisy paths ----

    private static void AssertExceptionClean(Exception? ex)
    {
        if (ex is null) return;
        Assert.DoesNotContain(Sentinel, ex.Message);
        Assert.DoesNotContain(Sentinel, ex.ToString());
        AssertExceptionClean(ex.InnerException);
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions) AssertExceptionClean(inner);
    }

    private static async Task<string> DriveAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, bool waitForInit)
    {
        var sink = new CaptureLogger();
        var options = Options();
        options.WaitForInitialization = waitForInit;
        var hooks = new SharedFeatureflipCore.CoreTestHooks { Handler = new ScriptedHandler(respond) };
        var raised = new List<Exception>();

        SharedFeatureflipCore? core = null;
        try
        {
            core = new SharedFeatureflipCore(Sentinel, options, sink, hooks);
            try
            {
                // A failed non-blocking init never completes the wait, so bound it.
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                await core.WaitForInitializationAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { raised.Add(ex); }
            var ctx = new EvaluationContext { UserId = "u1" };
            var detail = core.Evaluate("f", ctx, false);
            core.Evaluate("f", ctx, "wrong-type");
            core.Evaluate("no-such-flag", ctx, false);
            core.TrackEvaluation("f", ctx, detail);
            try { await core.FlushAsync(); } catch (Exception ex) { raised.Add(ex); }
            await Task.Delay(300); // let the poller hit the failing endpoint at least once
        }
        catch (Exception ex) { raised.Add(ex); }
        finally { core?.Dispose(); }

        foreach (var ex in raised) AssertExceptionClean(ex);
        return sink.All;
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(401, true)]
    [InlineData(401, false)]
    [InlineData(500, true)]
    [InlineData(500, false)]
    [InlineData(0, true)] // transport error quoting the Authorization header
    [InlineData(0, false)]
    public async Task NoPath_LogsOrThrowsTheKey(int status, bool waitForInit)
    {
        Func<HttpRequestMessage, HttpResponseMessage> respond = status switch
        {
            200 => r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, FlagsJson) : Json(HttpStatusCode.Accepted),
            0 => Transport,
            _ => _ => Json((HttpStatusCode)status),
        };

        var output = await DriveAsync(respond, waitForInit);
        Assert.DoesNotContain(Sentinel, output);
    }

    [Fact]
    public async Task EventsEndpointFailing_LogsFlushFailureWithoutTheKey()
    {
        var output = await DriveAsync(
            r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, FlagsJson) : Transport(r),
            waitForInit: true);
        Assert.DoesNotContain(Sentinel, output);
        Assert.Contains("[redacted]", output); // the POST error quoted the key and was redacted, not dropped
    }

    [Fact]
    public async Task ThrownInitException_InnerChain_DoesNotCarryTheKey()
    {
        var options = Options();
        options.WaitForInitialization = true;
        var hooks = new SharedFeatureflipCore.CoreTestHooks { Handler = new ScriptedHandler(Transport) };
        var ex = Assert.ThrowsAny<Exception>(() => new SharedFeatureflipCore(Sentinel, options, new CaptureLogger(), hooks));
        Assert.IsType<FeatureFlagInitializationException>(ex);
        Assert.NotNull(ex.InnerException);
        AssertExceptionClean(ex);
    }

    [Fact]
    public void Sanitize_CleanExceptionKeepsIdentity_LeakyOneIsReplaced()
    {
        var clean = new InvalidOperationException("nothing here");
        Assert.Same(clean, RedactingLogger.Sanitize(clean, Sentinel));

        var leaky = new AggregateException(new HttpRequestException("Authorization: " + Sentinel));
        var safe = RedactingLogger.Sanitize(leaky, Sentinel)!;
        AssertExceptionClean(safe);
        Assert.Contains("AggregateException", safe.Message);
    }

    [Fact]
    public void ExceptionWhoseToStringThrows_FailsClosed()
    {
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(sink, Sentinel);
        log.LogWarning(new ThrowingException(), "boom");
        Assert.Contains("ThrowingException", sink.All);
    }

    private sealed class ThrowingException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("nope");
    }

    [Fact]
    public void SecondKey_IsAddedWithoutNesting_AndBothAreRedacted()
    {
        const string other = "sdk_server_AnotherKeyThatMustNeverAppear-9876543210";
        var sink = new CaptureLogger();
        var log = RedactingLogger.Wrap(RedactingLogger.Wrap(sink, Sentinel), other);
        log.LogInformation("a {K} b {O}", Sentinel, other);
        Assert.DoesNotContain(Sentinel, sink.All);
        Assert.DoesNotContain(other, sink.All);
    }

    [Fact]
    public void DifferentOptionsOnSameKey_WarningDoesNotLogTheKey()
    {
        FeatureflipClient.ResetForTesting();
        var sink = new CaptureLogger();
        var o1 = Options(); o1.StartOffline = true;
        var o2 = Options(); o2.StartOffline = true; o2.FlushBatchSize = 7;
        try
        {
            using var a = FeatureflipClient.Get(Sentinel, o1, new WrapperLogger(sink));
            using var b = FeatureflipClient.Get(Sentinel, o2, new WrapperLogger(sink));
            Assert.Contains("options are ignored", sink.All);
        }
        finally { FeatureflipClient.ResetForTesting(); }
        Assert.DoesNotContain(Sentinel, sink.All);
    }

    private sealed class WrapperLogger : ILogger<FeatureflipClient>
    {
        private readonly ILogger _inner;
        public WrapperLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel l) => _inner.IsEnabled(l);
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
            => _inner.Log(l, e, s, x, f);
    }

    [Fact]
    public async Task TransportErrorQuotingKey_ReachesSinkRedacted()
    {
        // Proves the redactor, not just clean call sites: the exception text carries the key.
        var output = await DriveAsync(Transport, waitForInit: true);
        Assert.DoesNotContain(Sentinel, output);
    }
}
