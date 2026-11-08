using Featureflip.Client.Internal;
using Featureflip.Client.Tests.TestSupport;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

public class SnapshotPersisterTests
{
    private sealed class Harness
    {
        public MemoryStore Store { get; } = new();
        public LevelCapturingLogger Logger { get; } = new();
        public string State = "{\"state\":1}";
        public int Serializations;
        public long Wall = 1_800_000_000_000;
        public long Mono = 1_000;
        public int WorkersStarted;
        public SnapshotPersister Persister { get; }

        public Harness(Action<Action>? startWorker = null)
        {
            Persister = new SnapshotPersister(
                Store, "hash", () => { Serializations++; return State; }, Logger,
                () => Wall, () => Mono,
                startWorker ?? (work => { WorkersStarted++; work(); }));
        }
    }

    [Fact]
    public void Persist_WritesTheEnvelopeWithTheWriteTimeAndKeyHash()
    {
        var h = new Harness();

        h.Persister.Persist();

        var text = Assert.Single(h.Store.Puts);
        Assert.Equal("{\"v\":1,\"keyHash\":\"hash\",\"fetchedAt\":1800000000000,\"data\":{\"state\":1}}", text);
    }

    [Fact]
    public void Coalescing_HeldFirstPut_TurnsABurstIntoOneTrailingWriteThatReadsTheLatestState()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<bool>();
        h.Store.PutGates.Enqueue(gate);

        h.Persister.Persist();            // first write starts and is held
        h.State = "{\"state\":2}";
        h.Persister.Persist();            // queued
        h.State = "{\"state\":3}";
        h.Persister.Persist();            // coalesced into the same trailing write
        Assert.Single(h.Store.Puts);

        gate.SetResult(true);

        Assert.Equal(2, h.Store.Puts.Count);
        Assert.Contains("{\"state\":3}", h.Store.Puts[1], StringComparison.Ordinal);
        Assert.Equal(1, h.WorkersStarted);
    }

    [Fact]
    public void AfterTheWritesFinish_ANewPersistStartsANewWorker()
    {
        var h = new Harness();
        h.Persister.Persist();
        h.Persister.Persist();

        Assert.Equal(2, h.Store.Puts.Count);
        Assert.Equal(2, h.WorkersStarted);
    }

    [Fact]
    public void RefreshRule_UnchangedStateInsideTheWindow_IsSkipped_AndWrittenOnceItEnds()
    {
        var h = new Harness();
        h.Persister.PersistFull();
        Assert.Single(h.Store.Puts);

        h.Mono += 299_999;
        Assert.True(h.Persister.WithinRefreshWindow());
        h.Persister.PersistFull();
        Assert.Single(h.Store.Puts);

        h.Mono += 1; // exactly 300 s after the start of the last write
        Assert.False(h.Persister.WithinRefreshWindow());
        h.Persister.PersistFull();
        Assert.Equal(2, h.Store.Puts.Count);
    }

    [Fact]
    public void RefreshRule_ChangedStateIsWrittenImmediately_EvenInsideTheWindow()
    {
        var h = new Harness();
        h.Persister.PersistFull();

        h.Mono += 1_000;
        h.State = "{\"state\":2}";
        h.Persister.PersistFull();

        Assert.Equal(2, h.Store.Puts.Count);
    }

    [Fact]
    public void RefreshWindow_IsMeasuredFromTheStartOfTheLastWrite()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<bool>();
        h.Store.PutGates.Enqueue(gate);
        h.Persister.Persist();                 // write starts at Mono = 1000 and stays in flight

        h.Mono += 300_000;                     // the held write has been "running" for 300 s
        gate.SetResult(true);

        Assert.False(h.Persister.WithinRefreshWindow());
    }

    [Fact]
    public void ThrowingPut_IsWarnedAndDropped_AndLaterWritesStillHappen()
    {
        var h = new Harness();
        h.Store.PutThrows = new IOException("disk full");
        h.Persister.Persist();
        Assert.True(Warned(h.Logger, "write failed"));

        h.Store.PutThrows = null;
        h.Persister.Persist();

        Assert.Equal(2, h.Store.Puts.Count);
    }

    [Fact]
    public void SerializationFailure_IsWarned_AndDoesNotWedgeLaterWrites()
    {
        var h = new Harness();
        var fail = true;
        var persister = new SnapshotPersister(
            h.Store, "hash", () => fail ? throw new InvalidOperationException("boom") : "{}", h.Logger,
            () => h.Wall, () => h.Mono, work => work());

        persister.Persist();
        Assert.Empty(h.Store.Puts);
        Assert.True(Warned(h.Logger, "serialize"));

        fail = false;
        persister.Persist();
        Assert.Single(h.Store.Puts);
    }

    [Fact]
    public void WriterThatCannotStart_DoesNotThrow_AndDoesNotWedgeLaterWrites()
    {
        var fail = true;
        var h = new Harness(work => { if (fail) throw new OutOfMemoryException("no thread"); work(); });

        var ex = Record.Exception(() => h.Persister.Persist());
        Assert.Null(ex);
        Assert.True(Warned(h.Logger, "start"));

        fail = false;
        h.Persister.Persist();
        Assert.Single(h.Store.Puts);
    }

    [Fact]
    public void Shutdown_StartsNoNewWrite_AndNoTrailingWrite_ButDoesNotCancelTheOneInFlight()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<bool>();
        h.Store.PutGates.Enqueue(gate);
        h.Persister.Persist();
        h.Persister.Persist();        // trailing write queued behind the held one

        h.Persister.Shutdown();
        gate.SetResult(true);          // the in-flight write completes normally

        Assert.Single(h.Store.Puts);   // no trailing write after shutdown
        h.Persister.Persist();
        h.Persister.PersistFull();
        Assert.Single(h.Store.Puts);
    }

    [Fact]
    public async Task OnTheThreadPool_TheCallerIsNotBlockedByASlowStore()
    {
        var h = new Harness(work => Task.Run(work));
        var gate = new TaskCompletionSource<bool>();
        h.Store.PutGates.Enqueue(gate);

        h.Persister.Persist();      // returns at once although the put never completes
        await WaitUntil(() => h.Store.PutCount == 1);

        gate.SetResult(true);
    }
}
