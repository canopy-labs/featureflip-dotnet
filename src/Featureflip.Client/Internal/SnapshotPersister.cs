using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// Writes the in-memory state to the snapshot store without ever blocking the caller.
/// </summary>
/// <remarks>
/// <para>
/// Writes are coalesced: at most one <c>PutAsync</c> in flight plus one trailing write, and the
/// trailing write reads the state when it runs, so a burst of changes costs two puts and the
/// last one carries the final state. The put runs on the thread pool; nothing here is awaited
/// by initialization or evaluation.
/// </para>
/// <para>
/// Unchanged state is rewritten (to refresh <c>fetchedAt</c>) at most once per
/// <see cref="RefreshInterval"/>, measured from the START of the last write. Changed content is
/// never throttled. A hung <c>PutAsync</c> wedges later writes for the client: a known limit.
/// </para>
/// <para>
/// <see cref="Shutdown"/> neither waits for nor cancels a pending write, and no trailing write
/// starts after it. A failure to start the worker releases the in-flight flag, so it cannot
/// wedge persistence or break the apply that triggered it.
/// </para>
/// </remarks>
internal sealed class SnapshotPersister
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(300);

    private readonly ISnapshotStore _store;
    private readonly string _keyHash;
    private readonly Func<string> _serializeState;
    private readonly ILogger _logger;
    private readonly Func<long> _wallMs;
    private readonly Func<long> _monotonicMs;
    private readonly Action<Action> _startWorker;

    private readonly object _lock = new();
    private bool _inFlight;
    private bool _queued;
    private long? _lastWriteStartMs;
    private string? _lastData;
    private volatile bool _shutdown;

    public SnapshotPersister(
        ISnapshotStore store,
        string keyHash,
        Func<string> serializeState,
        ILogger logger,
        Func<long>? wallMs = null,
        Func<long>? monotonicMs = null,
        Action<Action>? startWorker = null)
    {
        _store = store;
        _keyHash = keyHash;
        _serializeState = serializeState;
        _logger = logger;
        _wallMs = wallMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _monotonicMs = monotonicMs ?? (() => TimestampToMilliseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        _startWorker = startWorker ?? (work => Task.Run(work));
    }

    /// <summary>
    /// Converts a <see cref="Stopwatch"/> timestamp to milliseconds without overflowing.
    /// </summary>
    /// <remarks>
    /// <c>timestamp * 1000</c> overflows a long once the timestamp passes 9.2e15. On Linux the
    /// frequency is 1e9 and the timestamp is host-boot based, so that happens after about 106.7
    /// days of host uptime, and the refresh window then stuck shut until the counter wrapped.
    /// </remarks>
    internal static long TimestampToMilliseconds(long timestamp, long frequency)
    {
        var ticksPerMillisecond = frequency / 1000;
        return ticksPerMillisecond >= 1
            ? timestamp / ticksPerMillisecond
            : timestamp * 1000 / frequency;
    }

    public bool WithinRefreshWindow()
    {
        lock (_lock)
        {
            return _lastWriteStartMs is { } last
                && _monotonicMs() - last < (long)RefreshInterval.TotalMilliseconds;
        }
    }

    /// <summary>After a full live apply: write changed content now, refresh unchanged content on the window.</summary>
    public void PersistFull()
    {
        if (_shutdown) return;

        if (WithinRefreshWindow())
        {
            string data;
            try
            {
                data = _serializeState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not serialize the flag state for the snapshot store");
                return;
            }

            lock (_lock)
            {
                if (string.Equals(data, _lastData, StringComparison.Ordinal)) return;
            }
        }

        Persist();
    }

    /// <summary>Schedules a write of the current state. Never throws, never blocks on the store.</summary>
    public void Persist()
    {
        if (_shutdown) return;

        lock (_lock)
        {
            if (_inFlight)
            {
                _queued = true;
                return;
            }

            _inFlight = true;
        }

        try
        {
            // The first envelope is built here, so the "last write" (state and start time) is
            // recorded at schedule time. An identical full apply arriving before the worker
            // thread has run then sees it and does not queue a second write.
            var first = BuildText();
            _startWorker(() => _ = RunAsync(first));
        }
        catch (Exception ex)
        {
            // Could not start the worker. Releasing the flag keeps later writes possible, and
            // swallowing here keeps a good fetch from failing over a write that never began.
            lock (_lock)
            {
                _inFlight = false;
                _queued = false;
                // Nothing was written, so forget the "last write" recorded at schedule time. Left
                // in place it would make the next identical full apply look already persisted.
                _lastData = null;
                _lastWriteStartMs = null;
            }

            _logger.LogWarning(ex, "Could not start the snapshot store write");
        }
    }

    public void Shutdown() => _shutdown = true;

    private async Task RunAsync(string? text)
    {
        try
        {
            while (true)
            {
                if (text != null)
                {
                    try
                    {
                        // Not cancellable: closing the client must not abort a write in progress.
                        await _store.PutAsync(text, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Snapshot store write failed; the write was dropped");
                    }
                }

                lock (_lock)
                {
                    if (!_queued || _shutdown)
                    {
                        _queued = false;
                        _inFlight = false;
                        return;
                    }

                    _queued = false;
                }

                // Re-checked after the decision above: a Shutdown between the lock and here must
                // not start a trailing write.
                if (_shutdown)
                {
                    lock (_lock)
                    {
                        _inFlight = false;
                        _queued = false;
                    }

                    return;
                }

                text = BuildText();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _inFlight = false;
                _queued = false;
            }

            _logger.LogWarning(ex, "Snapshot store write failed unexpectedly");
        }
    }

    private string? BuildText()
    {
        try
        {
            var fetchedAt = _wallMs();
            var data = _serializeState();
            lock (_lock)
            {
                _lastWriteStartMs = _monotonicMs();
                _lastData = data;
            }

            return SnapshotCodec.EncodeEnvelope(_keyHash, fetchedAt, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not serialize the flag state for the snapshot store");
            return null;
        }
    }
}
