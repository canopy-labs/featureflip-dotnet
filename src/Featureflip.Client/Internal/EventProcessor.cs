using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

internal sealed class EventProcessor : IDisposable
{
    /// <summary>
    /// Upper bound on buffered events.
    /// </summary>
    /// <remarks>
    /// Only reachable once <see cref="Requeue"/> starts returning batches faster than they
    /// drain — i.e. a sustained outage of the events endpoint. Past the bound the OLDEST
    /// events are shed, which caps memory and keeps the freshest analytics. It also means a
    /// long outage sheds the re-queued (stale) batches first rather than starving new
    /// events, so the SDK degrades to today's behavior instead of hoarding unsendable data.
    /// </remarks>
    public const int DefaultMaxQueueSize = 10_000;

    // A deque, not a ConcurrentQueue: Requeue has to push a failed batch back at the FRONT,
    // and the bound has to be enforced from the front too. Both need an explicit lock.
    private readonly LinkedList<SdkEvent> _queue = new();
    private readonly object _gate = new();
    private readonly TimeSpan _flushInterval;
    private readonly int _batchSize;
    private readonly int _maxQueueSize;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public EventProcessor(TimeSpan flushInterval, int batchSize, int maxQueueSize = DefaultMaxQueueSize)
    {
        _flushInterval = flushInterval;
        _batchSize = batchSize;
        _maxQueueSize = maxQueueSize > 0 ? maxQueueSize : DefaultMaxQueueSize;
    }

    public void Enqueue(SdkEvent evt)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _queue.AddLast(evt);
            TrimOldestLocked();
        }
    }

    public IReadOnlyList<SdkEvent> Drain()
    {
        lock (_gate)
        {
            if (_queue.Count == 0) return Array.Empty<SdkEvent>();
            var events = new List<SdkEvent>(_queue);
            _queue.Clear();
            return events;
        }
    }

    /// <summary>
    /// Takes up to <paramref name="maxCount"/> of the OLDEST events, leaving the rest queued.
    /// </summary>
    /// <remarks>
    /// The flush path uses this rather than <see cref="Drain"/> so one request can never
    /// carry more than a batch. That only became reachable once failed batches started
    /// being re-queued (#2456): before, a failure emptied the queue, so it never grew far
    /// past the batch size. After a sustained outage it can sit at the 10,000-event bound,
    /// and posting all of that at once risks a body the server rejects outright — a 413 is
    /// non-retryable, so the whole backlog would be dropped by the very path meant to
    /// preserve it.
    /// </remarks>
    public IReadOnlyList<SdkEvent> DrainBatch(int maxCount)
    {
        if (maxCount < 1) maxCount = 1;

        lock (_gate)
        {
            if (_queue.Count == 0) return Array.Empty<SdkEvent>();

            var take = Math.Min(maxCount, _queue.Count);
            var events = new List<SdkEvent>(take);
            for (var i = 0; i < take; i++)
            {
                events.Add(_queue.First!.Value);
                _queue.RemoveFirst();
            }
            return events;
        }
    }

    /// <summary>
    /// Returns a batch that failed to send to the FRONT of the queue, so the next flush
    /// retries it ahead of newer events and rough chronological order is preserved.
    /// </summary>
    /// <remarks>
    /// <see cref="Drain"/> empties the queue before the send, so without this a single
    /// transient failure discarded the batch outright (#2456). Callers must only re-queue
    /// failures a retry could plausibly fix — a rejected SDK key would otherwise be retried
    /// forever and pin the queue at its bound.
    /// </remarks>
    /// <returns>How many events were dropped to stay within the bound.</returns>
    public int Requeue(IReadOnlyList<SdkEvent> events)
    {
        if (events is null || events.Count == 0) return 0;

        lock (_gate)
        {
            // Disposed means shutdown is under way and nothing will flush again; report the
            // whole batch as dropped rather than growing a queue no one will drain.
            if (_disposed) return events.Count;

            for (var i = events.Count - 1; i >= 0; i--)
                _queue.AddFirst(events[i]);

            return TrimOldestLocked();
        }
    }

    /// <summary>Sheds oldest-first until the queue fits the bound. Caller holds <c>_gate</c>.</summary>
    private int TrimOldestLocked()
    {
        var dropped = 0;
        while (_queue.Count > _maxQueueSize)
        {
            _queue.RemoveFirst();
            dropped++;
        }
        return dropped;
    }

    public bool ShouldFlush()
    {
        lock (_gate) return _queue.Count >= _batchSize;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
