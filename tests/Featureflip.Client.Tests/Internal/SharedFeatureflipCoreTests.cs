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

        // A healthy/clean reconnect (failures 0 or 1) waits the base delay — the
        // anti-busy-loop guarantee: it is deterministic and strictly positive.
        Assert.Equal(baseDelay, SharedFeatureflipCore.ComputeReconnectDelay(0, baseDelay, maxDelay));
        Assert.Equal(baseDelay, SharedFeatureflipCore.ComputeReconnectDelay(1, baseDelay, maxDelay));
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
}
