using System.Collections.Generic;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class SharedFeatureflipCoreTests
{
    [Fact]
    public void NewCore_StartsAtRefcountOne()
    {
        using var core = SharedFeatureflipCore.CreateForTesting();
        Assert.Equal(1, core.RefCount);
    }

    [Fact]
    public void TryAcquire_IncrementsRefcount()
    {
        using var core = SharedFeatureflipCore.CreateForTesting();
        var acquired = core.TryAcquire();
        Assert.True(acquired);
        Assert.Equal(2, core.RefCount);
    }

    [Fact]
    public void Release_DecrementsRefcount()
    {
        var core = SharedFeatureflipCore.CreateForTesting();
        core.TryAcquire(); // refcount = 2
        core.Release();    // refcount = 1
        Assert.Equal(1, core.RefCount);
        core.Release();    // refcount = 0, core shuts down
    }

    [Fact]
    public void Release_AtZero_MarksCoreShutDown()
    {
        var core = SharedFeatureflipCore.CreateForTesting();
        core.Release(); // 1 -> 0
        Assert.True(core.IsShutDown);
    }

    [Fact]
    public void TryAcquire_AfterShutdown_ReturnsFalse()
    {
        var core = SharedFeatureflipCore.CreateForTesting();
        core.Release(); // shut down
        var acquired = core.TryAcquire();
        Assert.False(acquired);
        Assert.Equal(0, core.RefCount);
    }

    [Fact]
    public void TryAcquire_AfterOverRelease_ReturnsFalse()
    {
        var core = SharedFeatureflipCore.CreateForTesting();
        core.Release(); // 1 -> 0, shut down
        core.Release(); // spurious extra release — should be a no-op
        Assert.False(core.TryAcquire());
        Assert.True(core.IsShutDown);
    }

    [Fact]
    public void CoreEvaluate_FlagNotFound_ReturnsDefault()
    {
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());
        using var core = SharedFeatureflipCore.CreateForTesting(store);

        var context = new EvaluationContext { UserId = "user-1" };
        var detail = core.Evaluate("nonexistent", context, true);

        Assert.True(detail.Value);
        Assert.Equal(EvaluationReason.FlagNotFound, detail.Reason);
    }

    [Fact]
    public void CoreEvaluate_FlagExists_ReturnsEvaluatedValue()
    {
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { TestFlags.BoolFlag("my-flag", enabled: true, variation: "on") },
            new List<Segment>());
        using var core = SharedFeatureflipCore.CreateForTesting(store);

        var context = new EvaluationContext { UserId = "user-1" };
        var detail = core.Evaluate("my-flag", context, false);

        Assert.True(detail.Value);
    }

    [Fact]
    public async Task ProcessSse_Sync_ReplacesStore_DroppingStaleFlags()
    {
        var store = new FlagStore();
        // Pre-seed a stale flag the snapshot does NOT contain.
        store.Replace(
            new List<FlagConfiguration> { TestFlags.BoolFlag("flag-stale", enabled: true, variation: "on") },
            new List<Segment>());
        using var core = SharedFeatureflipCore.CreateForTesting(store);

        var snapshot = new GetFlagsResponse
        {
            Flags = new List<FlagConfiguration> { TestFlags.BoolFlag("flag-new", enabled: true, variation: "on") },
            Segments = new List<Segment>()
        };
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.Sync, Snapshot = snapshot });

        Assert.True(store.TryGetFlag("flag-new", out _));
        // Full REPLACE, not merge: a flag absent from the snapshot is dropped.
        Assert.False(store.TryGetFlag("flag-stale", out _));
    }

    [Fact]
    public async Task ProcessSse_Sync_WithNullFlags_DoesNotThrow_AndClearsStore()
    {
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { TestFlags.BoolFlag("flag-stale", enabled: true, variation: "on") },
            new List<Segment>());
        using var core = SharedFeatureflipCore.CreateForTesting(store);

        // A `"flags": null` sync payload deserializes Snapshot.Flags to null; the
        // sync handler must treat it as empty, not NRE (which would leave the
        // store stale).
        var snapshot = new GetFlagsResponse { Flags = null!, Segments = null! };
        await core.ProcessSseEventAsync(new SseEvent { Type = SseEventType.Sync, Snapshot = snapshot });

        Assert.Empty(store.GetAllFlags());
        Assert.Empty(store.GetAllSegments());
    }

    [Fact]
    public void ComputeReconnectDelay_IsNeverZero_Escalates_AndCapsWithJitter()
    {
        var baseDelay = TimeSpan.FromSeconds(3);
        var maxDelay = TimeSpan.FromSeconds(30);

        // A healthy/clean reconnect (failures 0 or 1) waits [base/2, base] — jittered
        // like every other level (see the lockstep test below), and still strictly
        // positive, which is the anti-busy-loop guarantee.
        var half = TimeSpan.FromTicks(baseDelay.Ticks / 2);
        Assert.InRange(SharedFeatureflipCore.ComputeReconnectDelay(0, baseDelay, maxDelay), half, baseDelay);
        Assert.InRange(SharedFeatureflipCore.ComputeReconnectDelay(1, baseDelay, maxDelay), half, baseDelay);
        Assert.True(SharedFeatureflipCore.ComputeReconnectDelay(0, baseDelay, maxDelay) > TimeSpan.Zero);

        // Escalates (with [d/2, d] jitter) but never below the base.
        var two = SharedFeatureflipCore.ComputeReconnectDelay(2, baseDelay, maxDelay);
        Assert.InRange(two, baseDelay, TimeSpan.FromSeconds(6));

        // Caps at maxDelay with [max/2, max] jitter for large failure counts.
        for (var failures = 6; failures <= 20; failures++)
        {
            var d = SharedFeatureflipCore.ComputeReconnectDelay(failures, baseDelay, maxDelay);
            Assert.InRange(d, TimeSpan.FromSeconds(15), maxDelay);
        }
    }

    [Fact]
    public void ComputeReconnectDelay_JittersTheFirstReconnect_SoASharedDropDoesNotReconnectInLockstep()
    {
        var baseDelay = TimeSpan.FromSeconds(3);
        var maxDelay = TimeSpan.FromSeconds(30);

        // The drops this backoff absorbs are FLEET-WIDE: a single edge event severs
        // every stream at once (#2457 — measured at a 2.5-3.0ms spread across both
        // eval-api pods). Every one of those clients then reconnects at
        // consecutiveFailures == 0, so that is exactly the path that must be
        // de-correlated. Returning a constant here republishes the drop's own
        // synchronisation as a reconnect spike one base delay later — observed in
        // prod as a 26-46ms reconnect spread, 3.1s after a 2.5-3.0ms drop spread.
        var samples = new HashSet<TimeSpan>();
        for (var i = 0; i < 200; i++)
        {
            samples.Add(SharedFeatureflipCore.ComputeReconnectDelay(0, baseDelay, maxDelay));
            samples.Add(SharedFeatureflipCore.ComputeReconnectDelay(1, baseDelay, maxDelay));
        }

        // Spread, not a single value: many instances waking together must scatter.
        Assert.True(
            samples.Count > 1,
            $"first-reconnect delay is deterministic ({samples.Count} distinct value(s)) — a fleet-wide drop reconnects in lockstep");

        // Same [d/2, d] window the escalating path already uses, so the delay never
        // exceeds today's and stays strictly positive (the anti-busy-loop guarantee).
        foreach (var d in samples)
        {
            Assert.InRange(d, TimeSpan.FromTicks(baseDelay.Ticks / 2), baseDelay);
            Assert.True(d > TimeSpan.Zero);
        }
    }
}
