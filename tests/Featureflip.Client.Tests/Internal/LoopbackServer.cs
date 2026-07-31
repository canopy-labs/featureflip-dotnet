using System.Net;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Teardown helper for the loopback <see cref="HttpListener"/> stub servers used by the
/// HTTP-client and recovery tests.
/// </summary>
/// <remarks>
/// <para>
/// Those tests run their stub server as a <c>Task.Run</c> loop parked in
/// <see cref="HttpListener.GetContextAsync"/>, which takes no <see cref="CancellationToken"/>
/// — the only way out is to tear the listener down. <see cref="HttpListener.Stop"/> is not
/// enough: on the Unix (fully managed) <see cref="HttpListener"/> implementation a pending
/// <c>GetContextAsync</c> is not reliably faulted by <c>Stop</c>, so a plain
/// <c>await serverTask</c> in a <c>finally</c> can wait forever.
/// </para>
/// <para>
/// That is not hypothetical: it wedged <c>csharp-sdk-ci</c> three times. All 354 tests
/// passed in ~5s and then one of the two target frameworks' test hosts never exited, so
/// the job ran the full 6-hour GitHub Actions limit and was killed with an empty log and
/// no annotation — a green suite that reports as a failure. Hence two rules for these
/// stub servers: close (dispose) the listener rather than just stopping it, and never join
/// the server loop without a bound.
/// </para>
/// </remarks>
internal static class LoopbackServer
{
    /// <summary>
    /// Default bound for <see cref="JoinAsync"/>. Generous next to the work a stub loop has
    /// left at teardown (observe the teardown, unwind) so a loaded CI runner cannot trip it,
    /// while staying far below any job timeout.
    /// </summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for a stub server loop to finish, but never indefinitely.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the loop completed within <paramref name="timeout"/>. Callers on a
    /// happy path can assert on this; callers in a <c>finally</c> should ignore it, because
    /// throwing there would mask the failure that is actually under test.
    /// </returns>
    /// <remarks>
    /// Faults are rethrown when the loop completes, preserving the "real server-side faults
    /// surface" behavior of the plain <c>await</c> this replaces. Expected teardown
    /// exceptions (cancellation, listener disposal) are the caller's to swallow, as before.
    /// </remarks>
    public static async Task<bool> JoinAsync(Task serverTask, TimeSpan? timeout = null)
    {
        var finished = await Task.WhenAny(serverTask, Task.Delay(timeout ?? JoinTimeout))
            .ConfigureAwait(false) == serverTask;

        if (finished)
        {
            await serverTask.ConfigureAwait(false);
            return true;
        }

        // Abandoned, so nothing will ever await it. Observe any later fault explicitly:
        // an unobserved faulted Task otherwise raises TaskScheduler.UnobservedTaskException
        // when it is finalized, which can fail an unrelated test later in the run.
        _ = serverTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return false;
    }
}
