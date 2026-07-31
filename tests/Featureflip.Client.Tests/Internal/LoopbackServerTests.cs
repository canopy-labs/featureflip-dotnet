using System.Diagnostics;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Guards the teardown contract of <see cref="LoopbackServer.JoinAsync"/>: it must never
/// wait indefinitely, and it must still surface a real server-side fault. An unbounded
/// join is what hung csharp-sdk-ci for the full 6-hour job limit after every test had
/// already passed.
/// </summary>
public class LoopbackServerTests
{
    [Fact]
    public async Task JoinAsync_LoopFinishes_ReturnsTrue()
    {
        Assert.True(await LoopbackServer.JoinAsync(Task.CompletedTask));
    }

    [Fact]
    public async Task JoinAsync_LoopFaults_RethrowsTheFault()
    {
        var boom = new InvalidOperationException("server loop blew up");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LoopbackServer.JoinAsync(Task.FromException(boom)));

        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task JoinAsync_LoopNeverFinishes_GivesUpInsteadOfHanging()
    {
        // Stands in for a loop parked in HttpListener.GetContextAsync, which takes no
        // CancellationToken and is not reliably faulted by HttpListener.Stop on Unix.
        var parkedForever = new TaskCompletionSource<bool>();
        var bound = TimeSpan.FromMilliseconds(200);

        var stopwatch = Stopwatch.StartNew();
        var finished = await LoopbackServer.JoinAsync(parkedForever.Task, bound);
        stopwatch.Stop();

        Assert.False(finished);
        // Loose upper bound: asserting anything near `bound` would flake on a loaded runner.
        // The point is that it returns at all rather than waiting on the task forever.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"JoinAsync took {stopwatch.Elapsed}, so it is not honoring its bound");
    }

    [Fact]
    public async Task JoinAsync_AbandonedLoopFaultsAfterwards_DoesNotThrowAtTheCallSite()
    {
        // The abandoned task's fault must be observed internally. Nothing awaits it again,
        // so an unobserved fault would otherwise surface via TaskScheduler's unobserved
        // exception path and fail an unrelated test later in the run.
        var late = new TaskCompletionSource<bool>();

        Assert.False(await LoopbackServer.JoinAsync(late.Task, TimeSpan.FromMilliseconds(200)));

        late.SetException(new InvalidOperationException("faulted after being abandoned"));

        // Faulting an already-abandoned task must not propagate anywhere observable.
        await Task.Delay(50);
    }
}
