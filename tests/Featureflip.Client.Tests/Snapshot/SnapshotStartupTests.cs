using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

/// <summary>
/// Core-level startup, persistence and recovery with a scripted transport and no background
/// data source, so nothing races the test's own listener registration.
/// </summary>
public class SnapshotStartupTests
{
    private static readonly EvaluationContext Ctx = new() { UserId = "u1" };

    private static bool Value(SharedFeatureflipCore core, string key = "flag-a")
        => core.Evaluate(key, Ctx, false).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- healthy start ----

    [Fact]
    public void HealthyStart_WritesTheSnapshot_AndNeverReadsTheStore()
    {
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());

        using var core = NewCore(Options(store), handler);

        Assert.True(core.IsInitialized);
        Assert.True(Value(core));
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        Assert.Equal(0, store.Gets);
        var text = Assert.Single(store.Puts);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("flag-a", doc.RootElement.GetProperty("data").GetProperty("flags")[0].GetProperty("key").GetString());
        Assert.DoesNotContain("sdk-key", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAStore_NothingIsWritten_AndStartupInfoStillReportsLive()
    {
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());

        using var core = NewCore(Options(null), handler);

        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
    }

    [Fact]
    public void BeforeAnyData_StartupInfoIsNone_WithNoReason()
    {
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(null, sync: false), handler);

        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
    }

    // ---- fallback ----

    [Fact]
    public void FetchError_FallsBackToTheSnapshot_WithoutWriting_AndNamesTheStatus()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() - 90_000) };
        var handler = new ScriptedHandler();
        handler.Fail(HttpStatusCode.ServiceUnavailable);
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.True(core.IsInitialized);
        Assert.True(Value(core));
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.Equal("snapshot", core.StartupInfo.Reason);
        Assert.InRange(core.StartupInfo.SnapshotAgeSeconds!.Value, 89, 95);
        Assert.Empty(store.Puts);
        Assert.True(Warned(logger, "503"));
        Assert.True(Warned(logger, "serving persisted snapshot"));
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("sdk-key", StringComparison.Ordinal));
    }

    [Fact]
    public void RevokedKey_IsDistinguishableFromAnUnreachableHost()
    {
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Fail(HttpStatusCode.Unauthorized);
        var logger = new LevelCapturingLogger();
        store.Text = Envelope("sdk-key", Now());

        using var core = NewCore(Options(store), handler, logger);

        Assert.True(Warned(logger, "401"));
    }

    [Fact]
    public void InitTimeout_FallsBackToTheSnapshot()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Hang();
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store, initTimeout: TimeSpan.FromMilliseconds(250)), handler, logger);

        Assert.True(Value(core));
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.True(Warned(logger, "timed out"));
    }

    [Fact]
    public void ExpiredSnapshot_ServesDefaults_WithTheExpiredReasonAndAge()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() - (2 * 86_400_000L)) };
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(store), handler);

        Assert.True(core.IsInitialized);
        Assert.False(Value(core));
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Equal("snapshot-expired", core.StartupInfo.Reason);
        Assert.InRange(core.StartupInfo.SnapshotAgeSeconds!.Value, 172_790, 172_900);
    }

    [Fact]
    public void MaxSnapshotAge_IsHonored()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() - 120_000) };
        var handler = new ScriptedHandler();
        handler.Fail();
        var options = Options(store);
        options.MaxSnapshotAge = TimeSpan.FromSeconds(60);

        using var core = NewCore(options, handler);

        Assert.Equal("snapshot-expired", core.StartupInfo.Reason);
    }

    [Fact]
    public void AgeAtTheLimit_IsStillUsable()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() - 30_000) };
        var handler = new ScriptedHandler();
        handler.Fail();
        var options = Options(store);
        options.MaxSnapshotAge = TimeSpan.FromSeconds(60);

        using var core = NewCore(options, handler);

        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
    }

    [Fact]
    public void FutureFetchedAt_WithinTolerance_IsAcceptedWithAgeZero()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() + 30_000) };
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(store), handler);

        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.Equal(0, core.StartupInfo.SnapshotAgeSeconds);
    }

    [Fact]
    public void NoStoredText_IsAPlainMiss_NoReasonAndNoWarningAboutTheSnapshot()
    {
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        Assert.Null(core.StartupInfo.SnapshotAgeSeconds);
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("snapshot-", StringComparison.Ordinal));
    }

    [Fact]
    public void FetchError_WithNothingStored_LogsTheCause_AndNeverTheKey()
    {
        var handler = new ScriptedHandler();
        handler.Fail(HttpStatusCode.ServiceUnavailable);
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(new MemoryStore()), handler, logger);

        Assert.True(Warned(logger, "Initial flag fetch failed"));
        Assert.True(Warned(logger, "503"));
        Assert.True(Warned(logger, "no snapshot stored"));
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("sdk-key", StringComparison.Ordinal));
    }

    [Fact]
    public void InitTimeout_WithNothingStored_LogsTheCause_AndNeverTheKey()
    {
        var handler = new ScriptedHandler();
        handler.Hang();
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(new MemoryStore(), initTimeout: TimeSpan.FromMilliseconds(250)), handler, logger);

        Assert.True(Warned(logger, "Initial flag fetch failed"));
        Assert.True(Warned(logger, "timed out"));
        Assert.DoesNotContain(logger.Snapshot(), e => e.Message.Contains("sdk-key", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedInitWithAStoreAndNoSnapshot_ReturnsAnInitializedDefaultsServingClient()
    {
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(new MemoryStore()), handler); // sync init: must not throw

        Assert.True(core.IsInitialized);
        Assert.True(core.WaitForInitializationAsync(Ct).IsCompletedSuccessfully);
        Assert.False(Value(core));
    }

    [Fact]
    public void WithoutAStore_AFailedSynchronousInitStillThrows()
    {
        var handler = new ScriptedHandler();
        handler.Fail();

        Assert.Throws<FeatureFlagInitializationException>(() => NewCore(Options(null), handler));
    }

    [Fact]
    public async Task WithoutAStore_AFailedAsyncInitStaysPending()
    {
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(null, sync: false), handler);
        await Task.Delay(150, Ct);

        Assert.False(core.IsInitialized);
        Assert.False(core.WaitForInitializationAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task AsyncInit_WithAStore_FallsBackAndCompletes()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(store, sync: false), handler);
        await core.WaitForInitializationAsync(Ct);

        Assert.True(Value(core));
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
    }

    // ---- invalid text through the real file store ----

    public static IEnumerable<object?[]> InvalidFileContents()
    {
        var good = Envelope("sdk-key", Now());
        yield return new object?[] { "garbage not json" };
        yield return new object?[] { good.Substring(0, good.Length / 2) };
        yield return new object?[] { Envelope("another-key", Now()) };
        yield return new object?[] { Envelope("sdk-key", Now(), baseUrl: "https://other.example.test") };
        yield return new object?[] { good.Replace("\"v\":1", "\"v\":2") };
        yield return new object?[] { Envelope("sdk-key", Now() + 3_600_000) };
        yield return new object?[] { "" };
    }

    [Theory]
    [MemberData(nameof(InvalidFileContents))]
    public async Task InvalidFile_IsSnapshotInvalid_ThroughTheRealFileStore(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, content, Ct);
            var handler = new ScriptedHandler();
            handler.Fail();
            var logger = new LevelCapturingLogger();

            using var core = NewCore(Options(new FileSnapshotStore(path)), handler, logger);

            Assert.Equal(StartupSource.None, core.StartupInfo.Source);
            Assert.Equal("snapshot-invalid", core.StartupInfo.Reason);
            Assert.False(Value(core));
            Assert.True(Warned(logger, "snapshot-invalid"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_IsAPlainMiss_ThroughTheRealFileStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-missing-" + Guid.NewGuid().ToString("N"));
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(new FileSnapshotStore(path)), handler);

        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
    }

    [Fact]
    public async Task OneFileStoreSharedByTwoSdkKeys_NeverLeaksAcrossThem()
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shared = new FileSnapshotStore(path);

            var up = new ScriptedHandler();
            up.Serve(FlagsJson("only-for-a"));
            using (NewCore(Options(shared), up, sdkKey: "key-a", startWorker: work => work())) { }
            Assert.NotNull(await shared.GetAsync(Ct));

            var down = new ScriptedHandler();
            down.Fail();
            using var other = NewCore(Options(shared), down, sdkKey: "key-b");
            using var same = NewCore(Options(shared), down, sdkKey: "key-a");

            Assert.Equal("snapshot-invalid", other.StartupInfo.Reason);
            Assert.False(Value(other, "only-for-a"));
            Assert.Equal(StartupSource.Snapshot, same.StartupInfo.Source);
            Assert.True(Value(same, "only-for-a"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- store failures never escape ----

    [Fact]
    public void ThrowingGet_IsAPlainMissWithAWarning()
    {
        var store = new MemoryStore { GetThrows = new IOException("redis down") };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.True(core.IsInitialized);
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        Assert.True(Warned(logger, "read failed"));
    }

    [Fact]
    public void ThrowingPut_NeverEscapesIntoInitOrEvaluation()
    {
        var store = new MemoryStore { PutThrows = new IOException("disk full") };
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.True(Value(core));
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.True(Warned(logger, "write failed"));
    }

    [Fact]
    public async Task HungGet_IsBoundedByTheInitTimeout_AndALateResultIsIgnored()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()), GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();

        var sw = Stopwatch.StartNew();
        using var core = NewCore(Options(store, initTimeout: TimeSpan.FromMilliseconds(300)), handler, logger);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 250, 3000);
        Assert.True(core.IsInitialized);
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        Assert.True(Warned(logger, "timed out"));

        gate.SetResult(true); // the late read finishes now: it must not be applied
        await Task.Delay(100, Ct);
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.False(Value(core));
    }

    [Fact]
    public async Task HungGet_ThatLaterFaults_RaisesNoUnobservedTaskException()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        var unobserved = 0;
        void Handle(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += Handle;
        try
        {
            using (NewCore(Options(store, initTimeout: TimeSpan.FromMilliseconds(150)), handler)) { }
            gate.SetException(new IOException("late failure"));
            await Task.Delay(50, Ct);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handle;
        }

        Assert.Equal(0, unobserved);
    }

    // ---- recovery ----

    [Fact]
    public async Task SnapshotStart_ThenLiveRecovery_FiresListenersForTheDiff_AndMovesStartupInfoToLive()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now() - 60_000, FlagsJson(on: false)) };
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(store), handler);
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.False(Value(core));

        var changed = new List<string>();
        using var subscription = core.OnFlagsChanged(keys => changed.AddRange(keys));
        handler.Serve(FlagsJson(on: true, version: 2));
        await core.PollOnceAsync(Ct);

        Assert.Equal(new[] { "flag-a" }, changed);
        Assert.True(Value(core));
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        Assert.Null(core.StartupInfo.SnapshotAgeSeconds);
        Assert.Single(store.Puts); // the live apply writes; the snapshot load never did
    }

    [Fact]
    public async Task LoadingASnapshot_FiresNoListeners()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Fail();
        var calls = 0;
        using var core = NewCore(Options(store, sync: false), handler);
        using var subscription = core.OnFlagsChanged(_ => Interlocked.Increment(ref calls));

        await core.WaitForInitializationAsync(Ct);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NoneStart_ThenLive_IsASilentBaseline()
    {
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(new MemoryStore()), handler);
        var calls = 0;
        using var subscription = core.OnFlagsChanged(_ => calls++);

        handler.Serve(FlagsJson());
        await core.PollOnceAsync(Ct);

        Assert.Equal(0, calls);
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.True(Value(core));
    }

    [Fact]
    public async Task ASnapshotReadThatFinishesAfterLiveData_DoesNotOverwriteIt()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { Text = Envelope("sdk-key", Now(), FlagsJson(on: false)), GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(store, sync: false, initTimeout: TimeSpan.FromSeconds(5)), handler);

        handler.Serve(FlagsJson(on: true));
        await core.PollOnceAsync(Ct);            // live data lands first
        gate.SetResult(true);                    // then the store read completes
        await core.WaitForInitializationAsync(Ct);

        Assert.True(Value(core));
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
    }

    // ---- delta persistence ----

    private static async Task<ScriptedHandler> LiveHandlerForDeltas()
    {
        var handler = new ScriptedHandler();
        handler.Respond = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/sdk/flags/flag-b", StringComparison.Ordinal))
            {
                return Task.FromResult(ScriptedHandler.Json("""{"key":"flag-b","version":1,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}"""));
            }

            return Task.FromResult(ScriptedHandler.Json(FlagsJson()));
        };
        await Task.CompletedTask;
        return handler;
    }

    [Fact]
    public async Task SingleFlagDeltas_PersistOnlyOnceTheClientIsLive()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = await LiveHandlerForDeltas();
        var live = handler.Respond;
        handler.Fail();
        using var core = NewCore(Options(store), handler);
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);

        handler.Respond = live;
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.FlagUpdated, FlagKey = "flag-b" });
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.FlagDeleted, FlagKey = "flag-b" });
        Assert.Empty(store.Puts); // snapshot start: deltas must not re-stamp stale data

        await core.PollOnceAsync(Ct); // first live full apply
        var afterFull = store.Puts.Count;
        Assert.Equal(1, afterFull);

        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.FlagUpdated, FlagKey = "flag-b" });
        Assert.Equal(afterFull + 1, store.Puts.Count);
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.FlagDeleted, FlagKey = "flag-b" });
        Assert.Equal(afterFull + 2, store.Puts.Count);
        Assert.DoesNotContain("flag-b", store.Puts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoneStart_DeltasDoNotPersistEither()
    {
        var store = new MemoryStore();
        var handler = await LiveHandlerForDeltas();
        var live = handler.Respond;
        handler.Fail();
        using var core = NewCore(Options(store), handler);

        handler.Respond = live;
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.FlagUpdated, FlagKey = "flag-b" });

        Assert.Empty(store.Puts);
    }

    // ---- refresh ----

    [Fact]
    public async Task Ping_WhileLive_RefreshesAtMostOncePerWindow()
    {
        long mono = 10_000;
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        using var core = NewCore(Options(store), handler, monotonicMs: () => mono);
        Assert.Single(store.Puts);

        var ping = new SseEvent { Type = SseEventType.Ping };
        mono += 299_999;
        await core.ProcessSseEventAsync(ping);
        Assert.Single(store.Puts);

        mono += 1;
        await core.ProcessSseEventAsync(ping);
        Assert.Equal(2, store.Puts.Count);

        await core.ProcessSseEventAsync(ping); // window restarted from the start of that write
        Assert.Equal(2, store.Puts.Count);
    }

    [Fact]
    public async Task Ping_OnASnapshotOrNoneStart_NeverRefreshes()
    {
        long mono = 0;
        var snapshotStore = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(snapshotStore), handler, monotonicMs: () => mono);
        mono += 10_000_000;

        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.Ping });

        Assert.Empty(snapshotStore.Puts);

        var noneStore = new MemoryStore();
        using var none = NewCore(Options(noneStore), handler, monotonicMs: () => mono);
        mono += 10_000_000;
        await none.ProcessSseEventAsync(new SseEvent { Type = SseEventType.Ping });
        Assert.Empty(noneStore.Puts);
    }

    [Fact]
    public async Task Polling_UnchangedStateInsideTheWindowIsSkipped_ChangedStateIsWritten()
    {
        long mono = 0;
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        using var core = NewCore(Options(store), handler, monotonicMs: () => mono);
        Assert.Single(store.Puts);

        mono += 30_000;
        await core.PollOnceAsync(Ct);
        await core.PollOnceAsync(Ct);
        Assert.Single(store.Puts);

        handler.Serve(FlagsJson(on: false));
        await core.PollOnceAsync(Ct);
        Assert.Equal(2, store.Puts.Count);

        mono += 300_000;
        await core.PollOnceAsync(Ct);
        Assert.Equal(3, store.Puts.Count);
    }

    // ---- persistence hygiene ----

    [Fact]
    public async Task CoalescingThroughTheCore_HeldFirstPut_ThenOneTrailingWriteWithTheLatestState()
    {
        var store = new MemoryStore();
        var gate = new TaskCompletionSource<bool>();
        store.PutGates.Enqueue(gate);
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson(on: true));
        using var core = NewCore(Options(store), handler);
        Assert.Single(store.Puts);            // the initial write is held in flight

        handler.Serve(FlagsJson(on: false));
        await core.PollOnceAsync(Ct);
        handler.Serve(FlagsJson(on: true, version: 3));
        await core.PollOnceAsync(Ct);
        Assert.Single(store.Puts);

        gate.SetResult(true);

        Assert.Equal(2, store.Puts.Count);
        Assert.Contains("\"version\":3", store.Puts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWriteStartsAfterDispose_AndDisposeDoesNotWaitForAPendingWrite()
    {
        var store = new MemoryStore();
        var gate = new TaskCompletionSource<bool>();
        store.PutGates.Enqueue(gate);
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var core = NewCore(Options(store), handler);
        handler.Serve(FlagsJson(on: false));
        await core.PollOnceAsync(Ct);                  // trailing write queued behind the held one

        var sw = Stopwatch.StartNew();
        core.Dispose();                                 // must return although the put is hung
        sw.Stop();
        gate.SetResult(true);

        Assert.True(sw.ElapsedMilliseconds < 4000);
        Assert.Single(store.Puts);
    }

    [Fact]
    public async Task WriterThatCannotStart_DoesNotBreakInit_OrWedgeLaterWrites()
    {
        var fail = true;
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger,
            startWorker: work => { if (fail) throw new InvalidOperationException("no threads"); work(); });

        Assert.True(core.IsInitialized);
        Assert.True(Value(core));
        Assert.True(Warned(logger, "start the snapshot store write"));

        fail = false;
        await core.PollOnceAsync(Ct);
        Assert.Single(store.Puts);
    }

    [Fact]
    public async Task ThePersistedState_ReadsBackAsTheSameFlagsThroughACleanCore()
    {
        var store = new MemoryStore();
        var up = new ScriptedHandler();
        up.Serve(FlagsJson("flag-a", on: true));
        using (NewCore(Options(store), up)) { }

        var down = new ScriptedHandler();
        down.Fail();
        using var second = NewCore(Options(store), down);

        Assert.True(Value(second));
        Assert.Equal(StartupSource.Snapshot, second.StartupInfo.Source);
        await Task.CompletedTask;
    }

    // ---- options ----

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveMaxSnapshotAge_IsAConfigError(int seconds)
    {
        var options = Options(new MemoryStore());
        options.MaxSnapshotAge = TimeSpan.FromSeconds(seconds);

        Assert.Throws<FeatureFlagInitializationException>(() => new SharedFeatureflipCore("k", options, new LevelCapturingLogger()));
    }

    [Fact]
    public void DefaultMaxSnapshotAge_IsOneDay() => Assert.Equal(TimeSpan.FromDays(1), new FeatureFlagOptions().MaxSnapshotAge);

    [Fact]
    public void TestStubClient_ExposesLiveStartupInfo()
    {
        using var client = new FeatureflipClient(new FlagStore(), new FlagEvaluator(), new FeatureFlagOptions());

        Assert.Equal(StartupSource.Live, client.StartupInfo.Source);
        Assert.Null(client.StartupInfo.Reason);
    }
}
