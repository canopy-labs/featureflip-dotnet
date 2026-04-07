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
}
