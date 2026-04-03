using System.Collections.Concurrent;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

internal sealed class EventProcessor : IDisposable
{
    private readonly ConcurrentQueue<SdkEvent> _queue = new();
    private readonly TimeSpan _flushInterval;
    private readonly int _batchSize;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public EventProcessor(TimeSpan flushInterval, int batchSize)
    {
        _flushInterval = flushInterval;
        _batchSize = batchSize;
    }

    public void Enqueue(SdkEvent evt)
    {
        if (_disposed) return;
        _queue.Enqueue(evt);
    }

    public IReadOnlyList<SdkEvent> Drain()
    {
        var events = new List<SdkEvent>();
        while (_queue.TryDequeue(out var evt))
            events.Add(evt);
        return events;
    }

    public bool ShouldFlush() => _queue.Count >= _batchSize;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
