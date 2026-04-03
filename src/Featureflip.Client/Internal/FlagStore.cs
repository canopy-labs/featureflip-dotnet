using System.Collections.Immutable;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Thread-safe store for flag and segment configurations using immutable collections
/// and atomic reference swapping.
/// </summary>
internal sealed class FlagStore
{
    private volatile FlagSnapshot _snapshot = FlagSnapshot.Empty;
    private readonly object _updateLock = new();

    /// <summary>
    /// Atomically replaces all flags and segments with the new configuration.
    /// This handles flag deletions by replacing the entire store.
    /// Duplicate keys are handled by keeping the last occurrence.
    /// </summary>
    public void Replace(IEnumerable<FlagConfiguration> flags, IEnumerable<Segment> segments)
    {
        // Handle potential duplicate keys by using GroupBy and taking last
        var flagDict = flags
            .GroupBy(f => f.Key)
            .ToImmutableDictionary(g => g.Key, g => g.Last());

        var segmentDict = segments
            .GroupBy(s => s.Key)
            .ToImmutableDictionary(g => g.Key, g => g.Last());

        lock (_updateLock)
        {
            _snapshot = new FlagSnapshot(flagDict, segmentDict);
        }
    }

    /// <summary>
    /// Atomically upserts flags and/or segments into the store.
    /// This is safe for concurrent updates - uses compare-and-swap semantics.
    /// </summary>
    public void Upsert(IEnumerable<FlagConfiguration>? flags = null, IEnumerable<Segment>? segments = null)
    {
        lock (_updateLock)
        {
            var current = _snapshot;
            var newFlags = current.Flags;
            var newSegments = current.Segments;

            if (flags != null)
            {
                var builder = current.Flags.ToBuilder();
                foreach (var flag in flags)
                {
                    builder[flag.Key] = flag;
                }
                newFlags = builder.ToImmutable();
            }

            if (segments != null)
            {
                var builder = current.Segments.ToBuilder();
                foreach (var segment in segments)
                {
                    builder[segment.Key] = segment;
                }
                newSegments = builder.ToImmutable();
            }

            _snapshot = new FlagSnapshot(newFlags, newSegments);
        }
    }

    /// <summary>
    /// Atomically removes a flag by key.
    /// </summary>
    public void RemoveFlag(string key)
    {
        lock (_updateLock)
        {
            var current = _snapshot;
            _snapshot = new FlagSnapshot(
                current.Flags.Remove(key),
                current.Segments);
        }
    }

    /// <summary>
    /// Atomically removes a segment by key.
    /// </summary>
    public void RemoveSegment(string key)
    {
        lock (_updateLock)
        {
            var current = _snapshot;
            _snapshot = new FlagSnapshot(
                current.Flags,
                current.Segments.Remove(key));
        }
    }

    public bool TryGetFlag(string key, out FlagConfiguration? flag)
        => _snapshot.Flags.TryGetValue(key, out flag);

    public bool TryGetSegment(string key, out Segment? segment)
        => _snapshot.Segments.TryGetValue(key, out segment);

    public IReadOnlyDictionary<string, FlagConfiguration> GetAllFlags() => _snapshot.Flags;
    public IReadOnlyDictionary<string, Segment> GetAllSegments() => _snapshot.Segments;

    /// <summary>
    /// Immutable snapshot of flags and segments using ImmutableDictionary
    /// for efficient structural sharing during updates.
    /// </summary>
    private sealed class FlagSnapshot
    {
        public static readonly FlagSnapshot Empty = new(
            ImmutableDictionary<string, FlagConfiguration>.Empty,
            ImmutableDictionary<string, Segment>.Empty);

        public ImmutableDictionary<string, FlagConfiguration> Flags { get; }
        public ImmutableDictionary<string, Segment> Segments { get; }

        public FlagSnapshot(
            ImmutableDictionary<string, FlagConfiguration> flags,
            ImmutableDictionary<string, Segment> segments)
        {
            Flags = flags;
            Segments = segments;
        }
    }
}
