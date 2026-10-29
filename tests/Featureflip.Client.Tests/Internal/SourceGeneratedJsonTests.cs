using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Guards for the move to source-generated System.Text.Json (#4301). The end-to-end proof
/// that the client works with reflection disabled lives in Featureflip.Client.ReflectionFreeTests;
/// these pin the properties that move made load-bearing.
/// </summary>
public class SourceGeneratedJsonTests
{
    public static IEnumerable<object[]> ShippedFlagOptions()
    {
        using var http = new FeatureFlagHttpClient("sdk-key", new FeatureFlagOptions());
        yield return new object[] { "polling", http.JsonOptions };
        yield return new object[] { "streaming", SseStreamReader.SnapshotJsonOptions };
    }

    /// <summary>
    /// The stock JsonStringEnumConverter that used to back the tolerant ones was removed (it
    /// needs runtime code generation). It was unreachable, but it was also what would have
    /// caught a NEW wire enum nobody wrote a tolerant converter for. Without it such an enum
    /// would fall back to the generated default and accept integers by ordinal, the silent
    /// mis-typing #2315 closed. This walks the payload graph instead of listing enums by
    /// hand, so a new one is covered the moment a model references it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedFlagOptions))]
    public void EveryWireEnum_ResolvesToATolerantConverter(string transport, JsonSerializerOptions options)
    {
        var enums = ReachableEnums(typeof(GetFlagsResponse)).ToList();
        Assert.NotEmpty(enums);

        foreach (var type in enums)
        {
            var converter = options.GetTypeInfo(type).Converter;
            Assert.True(converter.GetType().Name.StartsWith("Tolerant", StringComparison.Ordinal),
                $"[{transport}] {type.Name} resolves to {converter.GetType().Name}; register a Tolerant* converter for it");
        }
    }

    /// <summary>
    /// The source generator sets init-only properties through an object initializer that
    /// assigns all of them, so an ABSENT property became null instead of keeping its
    /// <c>= new()</c> initializer, and a flag sent without <c>prerequisites</c> crashed the
    /// evaluator. The models use setters for that reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedFlagOptions))]
    public void AbsentCollections_KeepTheirInitializers(string transport, JsonSerializerOptions options)
    {
        const string json = """
            {"flags":[{"key":"f","rules":[{"id":"r","conditionGroups":[{"operator":"And","conditions":[{"attribute":"a","operator":"Equals"}]}]}]}],
             "segments":[{"key":"s"}]}
            """;

        var parsed = JsonSerializer.Deserialize<GetFlagsResponse>(json, options)!;

        var flag = Assert.Single(parsed.Flags);
        Assert.NotNull(flag.Variations);
        Assert.NotNull(flag.Prerequisites);
        Assert.Equal(string.Empty, flag.OffVariation);
        var condition = Assert.Single(Assert.Single(Assert.Single(flag.Rules).ConditionGroups).Conditions);
        Assert.NotNull(condition.Values);
        Assert.NotNull(Assert.Single(parsed.Segments).Conditions);
        Assert.Equal(string.Empty, parsed.Environment);
        _ = transport;
    }

    /// <summary>
    /// What the shipped options produced before #4301: reflection metadata over the same
    /// naming policy and converters, plus the stock enum converter that has since gone.
    /// </summary>
    private static JsonSerializerOptions ReflectionReplica() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters =
        {
            new TolerantConditionOperatorConverter(),
            new TolerantFlagTypeConverter(),
            new TolerantServeTypeConverter(),
            new TolerantConditionLogicConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)
        }
    };

    [Fact]
    public void EventPost_BytesMatchTheReflectionPath()
    {
        var request = new SendEventsRequest
        {
            Events = new[]
            {
                new SdkEvent
                {
                    Type = "Evaluation", FlagKey = "f", UserId = "u", Variation = "on",
                    Timestamp = new DateTime(2026, 10, 26, 1, 2, 3, 456, DateTimeKind.Utc),
                    Reason = "rule-match", RuleId = "r-1",
                    Metadata = new Dictionary<string, JsonElement> { ["k"] = JsonDocument.Parse("[1,\"x\"]").RootElement.Clone() }
                },
                // Anonymous, every optional absent: userId stays as null, the rest are omitted.
                new SdkEvent { Type = "Evaluation", FlagKey = "g", Timestamp = DateTime.UnixEpoch, Reason = "fallthrough" }
            }
        };
        using var http = new FeatureFlagHttpClient("sdk-key", new FeatureFlagOptions());

        Assert.Equal(
            JsonSerializer.Serialize(request, ReflectionReplica()),
            JsonSerializer.Serialize(request, http.JsonOptions));
    }

    [Theory]
    [MemberData(nameof(ShippedFlagOptions))]
    public void GoldenSnapshot_ParsesIdenticallyToTheReflectionPath(string transport, JsonSerializerOptions options)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "snapshot.json"));
        var replica = ReflectionReplica();

        var expected = JsonSerializer.Deserialize<GetFlagsResponse>(json, replica)!;
        var actual = JsonSerializer.Deserialize<GetFlagsResponse>(json, options)!;

        Assert.True(actual.Flags.Count > 0, $"[{transport}] fixture parsed to no flags");
        Assert.Equal(JsonSerializer.Serialize(expected, replica), JsonSerializer.Serialize(actual, replica));
    }

    [Fact]
    public void WireModels_HaveNoInitOnlySetters()
    {
        // The structural form of the guard above, for a property no fixture happens to omit.
        var offenders = ReachableTypes(typeof(GetFlagsResponse))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "init-only on deserialized wire models: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), true)]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(ObjectDisposedException), false)]
    [InlineData(typeof(HttpRequestException), false)]
    [InlineData(typeof(JsonException), false)]
    [InlineData(typeof(IOException), false)]
    [InlineData(typeof(TimeoutException), false)]
    [InlineData(typeof(TaskCanceledException), false)]
    public void PermanentInitFailure_Classification(Type exceptionType, bool permanent)
    {
        var ex = exceptionType == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("x")
            : (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Equal(permanent, SharedFeatureflipCore.IsPermanentInitFailure(ex));
    }

    /// <summary>
    /// An initial load that fails the same way on every retry used to leave
    /// <see cref="SharedFeatureflipCore.WaitForInitializationAsync"/> pending forever, which is
    /// what a reflection-free app saw (#4301). An unparseable charset is a convenient
    /// deterministic stand-in: System.Net.Http.Json reports it as InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task BackgroundInit_PermanentFailure_FaultsTheWait()
    {
        await using var server = new FlagsStub(_ => (200, "application/json; charset=not-a-charset", FlagsJson));
        using var core = new SharedFeatureflipCore("sdk-key", AsyncOptions(server.BaseUrl), NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<FeatureFlagInitializationException>(
            () => core.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.False(core.IsInitialized);
    }

    [Fact]
    public async Task BackgroundInit_PermanentFailure_WithStartOffline_CompletesOffline()
    {
        await using var server = new FlagsStub(_ => (200, "application/json; charset=not-a-charset", FlagsJson));
        var options = AsyncOptions(server.BaseUrl);
        options.StartOffline = true;
        using var core = new SharedFeatureflipCore("sdk-key", options, NullLogger.Instance);

        await core.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(core.IsInitialized);
    }

    [Fact]
    public async Task BackgroundInit_TransientFailure_StillRetriesUntilLoaded()
    {
        await using var server = new FlagsStub(n => n == 1
            ? (503, "text/plain", "unavailable")
            : (200, "application/json", FlagsJson));
        using var core = new SharedFeatureflipCore("sdk-key", AsyncOptions(server.BaseUrl), NullLogger.Instance);

        await core.WaitForInitializationAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.True(core.IsInitialized);
        Assert.True(core.Evaluate("f", new EvaluationContext { UserId = "u" }, false).Value);
    }

    private const string FlagsJson = """
        {"environment":"t","version":1,"segments":[],"flags":[{"key":"f","version":1,"type":"Boolean","enabled":true,
         "variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],
         "fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}]}
        """;

    private static FeatureFlagOptions AsyncOptions(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        Streaming = false,
        WaitForInitialization = false,
        // The refresh loop starts polling InitTimeout + 5s after a failed background init.
        InitTimeout = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromMinutes(10),
        FlushInterval = TimeSpan.FromMinutes(10)
    };

    private static IEnumerable<Type> ReachableEnums(Type root) =>
        ReachableTypes(root)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => t.IsEnum)
            .Distinct();

    private static IEnumerable<Type> ReachableTypes(Type root)
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(new[] { root });
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!seen.Add(type)) continue;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var t = property.PropertyType;
                var element = t.IsGenericType ? t.GetGenericArguments()[0] : t;
                if (element.Namespace == typeof(GetFlagsResponse).Namespace && element.IsClass)
                {
                    pending.Enqueue(element);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Serves <c>/v1/sdk/flags</c> from a per-request-number responder; anything else 404s.
    /// Torn down per the LoopbackServer rules (close, bounded join).
    /// </summary>
    private sealed class FlagsStub : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;
        private int _flagRequests;

        public string BaseUrl { get; }

        public FlagsStub(Func<int, (int Status, string ContentType, string Body)> respond)
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
                try { listener.Start(); _listener = listener; BaseUrl = candidate; break; }
                catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
            }

            _loop = Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }

                    try
                    {
                        if (ctx.Request.Url!.AbsolutePath.EndsWith("/v1/sdk/flags", StringComparison.Ordinal))
                        {
                            var (status, contentType, body) = respond(Interlocked.Increment(ref _flagRequests));
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentType = contentType;
                            var bytes = Encoding.UTF8.GetBytes(body);
                            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                    }
                    catch
                    {
                        // Client went away mid-response.
                    }
                    finally
                    {
                        try { ctx.Response.Close(); } catch { }
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            try { _listener.Close(); } catch { }
            await LoopbackServer.JoinAsync(_loop);
        }
    }
}
