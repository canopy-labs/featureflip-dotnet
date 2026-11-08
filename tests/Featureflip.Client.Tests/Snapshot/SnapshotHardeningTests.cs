using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

/// <summary>
/// <c>umask</c> is process-wide, so the one test that widens it runs in a collection xUnit never
/// runs alongside another test class.
/// </summary>
[CollectionDefinition("ProcessUmask", DisableParallelization = true)]
public sealed class ProcessUmaskCollection;

[Collection("ProcessUmask")]
public class SnapshotHardeningTests
{
    private static readonly EvaluationContext Ctx = new() { UserId = "u1" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static bool Value(SharedFeatureflipCore core, string key = "flag-a") => core.Evaluate(key, Ctx, false).Value;

    private static GetFlagsResponse Parse(string json)
        => JsonSerializer.Deserialize<GetFlagsResponse>(json, SseStreamReader.SnapshotJsonOptions)!;

    // ---- refresh clock ----

    [Fact]
    public void TimestampConversion_DoesNotOverflowAfterMonthsOfHostUptime()
    {
        // 9.3e15 ns is about 107.6 days of CLOCK_MONOTONIC on Linux (frequency 1e9). The old
        // `timestamp * 1000 / frequency` overflowed a long here and went negative.
        Assert.Equal(9_300_000_000L, SnapshotPersister.TimestampToMilliseconds(9_300_000_000_000_000L, 1_000_000_000L));
        Assert.Equal(1_500L, SnapshotPersister.TimestampToMilliseconds(15_000_000L, 10_000_000L));
        Assert.Equal(2_000L, SnapshotPersister.TimestampToMilliseconds(200L, 100L)); // sub-kilohertz frequency
    }

    [Fact]
    public void RefreshWindow_StillWorksAtLargeUptimeTimestamps()
    {
        long ts = 9_300_000_000_000_000L;
        var persister = new SnapshotPersister(
            new MemoryStore(), "h", () => "{}", new LevelCapturingLogger(),
            () => 1, () => SnapshotPersister.TimestampToMilliseconds(ts, 1_000_000_000L), work => work());
        persister.Persist();
        Assert.True(persister.WithinRefreshWindow());

        ts += 300_000L * 1_000_000L; // 300 s later
        Assert.False(persister.WithinRefreshWindow());
    }

    // ---- netstandard2.0 owner-only create ----

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);

    [Fact]
    public void PosixOpen_CreatesOwnerOnlyEvenUnderAWideningUmask()
    {
        if (OperatingSystem.IsWindows() || !PosixOpen.IsSupported()) return;
        var dir = Path.Combine(Path.GetTempPath(), "ff-posix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = Umask(0); // widen: a default-mode create would now be 0666
        try
        {
            var plain = Path.Combine(dir, "plain");
            using (new FileStream(plain, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite,
                File.GetUnixFileMode(plain)); // proves the scenario really widens

            var owned = Path.Combine(dir, "owned");
            using (var stream = PosixOpen.CreateExclusiveOwnerOnly(owned))
            {
                stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
            }

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(owned));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(owned));
            Assert.Throws<IOException>(() => PosixOpen.CreateExclusiveOwnerOnly(owned)); // O_EXCL
        }
        finally
        {
            Umask(old);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- codec ----

    [Fact]
    public void LoneSurrogateFromACustomStore_IsSnapshotInvalid_NotAnException()
    {
        var store = new MemoryStore { Text = "{\"v\":1,\"x\":\"\ud800\"}" };
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(store), handler);

        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
        Assert.Equal("snapshot-invalid", core.StartupInfo.Reason);
    }

    [Fact]
    public async Task NonUtf8BytesInTheFile_AreSnapshotInvalid_ThroughTheRealFileStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ff-bytes-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 0xC3, 0x28, 0xA0, 0xA1, 0x7B }, Ct);
            var handler = new ScriptedHandler();
            handler.Fail();

            using var core = NewCore(Options(new FileSnapshotStore(path)), handler);

            Assert.Equal("snapshot-invalid", core.StartupInfo.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- full-apply paths persist ----

    [Fact]
    public async Task SseSync_Persists_AndAfterASnapshotStartMovesToLiveWithTheListenerDiff()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now(), FlagsJson(on: false)) };
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(store), handler);
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        var changed = new List<string>();
        using var subscription = core.OnFlagsChanged(keys => changed.AddRange(keys));

        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.Sync, Snapshot = Parse(FlagsJson(on: true, version: 2)) });

        Assert.Equal(new[] { "flag-a" }, changed);
        Assert.True(Value(core));
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.Null(core.StartupInfo.Reason);
        var text = Assert.Single(store.Puts);
        Assert.Contains("\"version\":2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseSegmentUpdated_RefetchesAndPersists()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now(), FlagsJson(on: false)) };
        var handler = new ScriptedHandler();
        handler.Fail();
        using var core = NewCore(Options(store), handler);
        var changed = new List<string>();
        using var subscription = core.OnFlagsChanged(keys => changed.AddRange(keys));

        handler.Serve(FlagsJson(on: true, version: 5));
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.SegmentUpdated });

        Assert.Equal(new[] { "flag-a" }, changed);
        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.Contains("\"version\":5", Assert.Single(store.Puts), StringComparison.Ordinal);
    }

    // ---- write scheduling ----

    [Fact]
    public async Task HealthyStart_WritesOnce_EvenWhenTheImmediateFirstPollArrivesBeforeTheWorkerRuns()
    {
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var deferred = new List<Action>();
        using var core = NewCore(Options(store), handler, startWorker: deferred.Add);

        await core.PollOnceAsync(Ct); // the same full apply, before the first worker has run
        foreach (var work in deferred.ToArray()) work();

        Assert.Single(store.Puts);
    }

    [Fact]
    public async Task AnInFlightPollThatFinishesAfterShutdown_StartsNoWrite()
    {
        var store = new MemoryStore();
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var core = NewCore(Options(store), handler);
        Assert.Single(store.Puts);

        core.Dispose();
        handler.Serve(FlagsJson(on: false));
        await core.PollOnceAsync(Ct);

        Assert.Single(store.Puts);
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForAHeldPut_AndNoTrailingWriteFollows()
    {
        var store = new MemoryStore();
        var gate = new TaskCompletionSource<bool>();
        store.PutGates.Enqueue(gate);
        var handler = new ScriptedHandler();
        handler.Serve(FlagsJson());
        var core = NewCore(Options(store), handler, startWorker: work => Task.Run(work));
        await WaitUntil(() => store.PutCount == 1);        // the first put is genuinely in flight
        handler.Serve(FlagsJson(on: false));
        await core.PollOnceAsync(Ct);                       // trailing write queued behind it

        var dispose = Task.Run(core.Dispose, Ct);
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10), Ct));

        Assert.Same(dispose, finished);                     // returned while the put is still held
        Assert.False(gate.Task.IsCompleted);
        gate.SetResult(true);
        await Task.Delay(100, Ct);
        Assert.Single(store.Puts);
    }

    // ---- init paths ----

    [Fact]
    public async Task BackgroundInit_WithAHungFetch_IsBoundedByTheInitTimeout()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Hang();

        var sw = Stopwatch.StartNew();
        using var core = NewCore(Options(store, sync: false, initTimeout: TimeSpan.FromMilliseconds(300)), handler);
        await core.WaitForInitializationAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 250, 5000);
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
    }

    [Fact]
    public async Task ACooperativeStore_SeesItsTokenCancelledWhenTheReadTimesOut()
    {
        var store = new MemoryStore { GetGate = new TaskCompletionSource<bool>().Task };
        var handler = new ScriptedHandler();
        handler.Fail();

        using var core = NewCore(Options(store, initTimeout: TimeSpan.FromMilliseconds(200)), handler);

        await WaitUntil(() => store.GetWasCancelled);
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
    }

    [Fact]
    public void StartOffline_WithAStore_StillStartsFromTheSnapshot()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Fail();
        var options = Options(store);
        options.StartOffline = true;

        using var core = NewCore(options, handler);

        Assert.True(core.IsInitialized);
        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.True(Value(core));
    }

    [Fact]
    public void StartOffline_WithAStoreAndNoSnapshot_IsInitializedWithDefaults()
    {
        var handler = new ScriptedHandler();
        handler.Fail();
        var options = Options(new MemoryStore());
        options.StartOffline = true;

        using var core = NewCore(options, handler);

        Assert.True(core.IsInitialized);
        Assert.Equal(StartupSource.None, core.StartupInfo.Source);
    }

    [Fact]
    public void ANullFlagsBody_IsAFailedFetchForStartup_SoTheStoreIsConsulted()
    {
        var store = new MemoryStore { Text = Envelope("sdk-key", Now()) };
        var handler = new ScriptedHandler();
        handler.Serve("null");
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.Equal(StartupSource.Snapshot, core.StartupInfo.Source);
        Assert.True(Value(core));
        Assert.True(Warned(logger, "empty"));
        Assert.Empty(store.Puts);
    }

    [Fact]
    public void WithoutAStore_AFailedInitialFetchLogsItsCause()
    {
        var handler = new ScriptedHandler();
        handler.Fail(HttpStatusCode.Forbidden);
        var logger = new LevelCapturingLogger();

        Assert.Throws<FeatureFlagInitializationException>(() => NewCore(Options(null), handler, logger));

        Assert.True(Warned(logger, "403"));
    }

    [Fact]
    public async Task LiveDataArrivingDuringASlowStoreRead_SuppressesTheIgnoringSnapshotWarning()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { Text = "garbage", GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();
        using var core = NewCore(Options(store, sync: false, initTimeout: TimeSpan.FromSeconds(5)), handler, logger);

        handler.Serve(FlagsJson());
        await core.PollOnceAsync(Ct);
        gate.SetResult(true);
        await core.WaitForInitializationAsync(Ct);

        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.False(Warned(logger, "ignoring persisted snapshot"));
        Assert.Null(core.StartupInfo.Reason);
    }

    [Fact]
    public void DefaultStartupSource_IsNone() => Assert.Equal(StartupSource.None, default(StartupSource));

    [Fact]
    public async Task LiveDataArrivingDuringASlowStoreRead_SuppressesTheTimedOutWarning()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();
        using var core = NewCore(Options(store, sync: false, initTimeout: TimeSpan.FromMilliseconds(400)), handler, logger);

        handler.Serve(FlagsJson());
        await core.PollOnceAsync(Ct);                 // live data lands while the read is hung
        await core.WaitForInitializationAsync(Ct);    // the read then times out

        Assert.Equal(StartupSource.Live, core.StartupInfo.Source);
        Assert.False(Warned(logger, "snapshot store read"));
        gate.SetResult(true);
    }

    [Fact]
    public async Task LiveDataArrivingDuringASlowStoreRead_SuppressesTheReadFailedWarning()
    {
        var gate = new TaskCompletionSource<bool>();
        var store = new MemoryStore { GetGate = gate.Task, IgnoreCancellationOnGet = true };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();
        using var core = NewCore(Options(store, sync: false, initTimeout: TimeSpan.FromSeconds(10)), handler, logger);

        handler.Serve(FlagsJson());
        await core.PollOnceAsync(Ct);
        gate.SetException(new IOException("redis went away"));
        await core.WaitForInitializationAsync(Ct);

        Assert.False(Warned(logger, "snapshot store read"));
    }

    [Fact]
    public void ASlowStoreReadThatFailsWithNoLiveData_StillWarns()
    {
        var store = new MemoryStore { GetThrows = new IOException("redis down") };
        var handler = new ScriptedHandler();
        handler.Fail();
        var logger = new LevelCapturingLogger();

        using var core = NewCore(Options(store), handler, logger);

        Assert.True(Warned(logger, "snapshot store read failed"));
    }
}
