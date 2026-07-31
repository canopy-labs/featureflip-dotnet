using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Called with the keys of every flag affected by a single store mutation.
/// Batched — one call per mutation, not one per key, so a poll that changes 40
/// flags notifies once. Never called with an empty collection.
/// </summary>
internal delegate void FlagChangeListener(IReadOnlyCollection<string> keys);

/// <summary>
/// Thread-safe store for flag and segment configurations using immutable collections
/// and atomic reference swapping.
/// </summary>
internal sealed class FlagStore
{
    private volatile FlagSnapshot _snapshot = FlagSnapshot.Empty;
    private readonly object _updateLock = new();

    // Copy-on-write so notification can iterate without holding a lock. Mutated only
    // under _listenerLock; read via a single volatile field read.
    private volatile FlagChangeListener[] _listeners = Array.Empty<FlagChangeListener>();
    private readonly object _listenerLock = new();

    /// <summary>
    /// Whether a snapshot has been loaded. The first <see cref="Replace"/> establishes
    /// the baseline rather than reporting every flag as changed — a cold load is not a
    /// configuration change.
    /// </summary>
    private bool _hasSnapshot;

    /// <summary>
    /// Atomically replaces all flags and segments with the new configuration.
    /// This handles flag deletions by replacing the entire store.
    /// Duplicate keys are handled by keeping the last occurrence.
    /// </summary>
    public void Replace(IEnumerable<FlagConfiguration>? flags, IEnumerable<Segment>? segments)
    {
        // Treat null as empty: a `"flags": null` / `"segments": null` payload
        // deserializes the response collections to null (System.Text.Json
        // overrides the property default), and a null sync snapshot must clear
        // the store rather than NRE. Handle duplicate keys by keeping the last.
        var flagDict = (flags ?? Enumerable.Empty<FlagConfiguration>())
            .GroupBy(f => f.Key)
            .ToImmutableDictionary(g => g.Key, g => g.Last());

        var segmentDict = (segments ?? Enumerable.Empty<Segment>())
            .GroupBy(s => s.Key)
            .ToImmutableDictionary(g => g.Key, g => g.Last());

        IReadOnlyCollection<string>? changed;
        lock (_updateLock)
        {
            // The diff needs the pre-swap snapshot, so it has to run inside the lock.
            changed = _hasSnapshot ? DiffSnapshot(_snapshot, flagDict, segmentDict) : null;
            _snapshot = new FlagSnapshot(flagDict, segmentDict);
            _hasSnapshot = true;
        }

        Notify(changed);
    }

    /// <summary>
    /// Atomically upserts flags and/or segments into the store.
    /// This is safe for concurrent updates - uses compare-and-swap semantics.
    /// </summary>
    public void Upsert(IEnumerable<FlagConfiguration>? flags = null, IEnumerable<Segment>? segments = null)
    {
        IReadOnlyCollection<string>? changed;

        lock (_updateLock)
        {
            var current = _snapshot;
            var newFlags = current.Flags;
            var newSegments = current.Segments;
            HashSet<string>? affected = null;
            HashSet<string>? changedSegments = null;

            // Every key handed to Upsert counts as changed, with no version comparison.
            // Upsert is reached only from an SSE `flag.created`/`flag.updated` delta, and
            // the server emits those only for a flag that actually changed — so the event
            // itself is the signal. Comparing versions was both redundant and lossy: the
            // wire version is second-granular (eval-api divides epoch ms by 1000), so two
            // edits to one flag inside the same wall-clock second carry an identical
            // version, and the second edit's notification was dropped even though its
            // config was applied. A consumer caching per key then served the pre-edit
            // values indefinitely. Mirrors js-sdk's `FlagStore.upsert`, which notifies
            // unconditionally for the same reason (#2090).
            if (flags != null)
            {
                var builder = current.Flags.ToBuilder();
                foreach (var flag in flags)
                {
                    (affected ??= new HashSet<string>(StringComparer.Ordinal)).Add(flag.Key);
                    builder[flag.Key] = flag;
                }
                newFlags = builder.ToImmutable();
            }

            if (segments != null)
            {
                var builder = current.Segments.ToBuilder();
                foreach (var segment in segments)
                {
                    (changedSegments ??= new HashSet<string>(StringComparer.Ordinal)).Add(segment.Key);
                    builder[segment.Key] = segment;
                }
                newSegments = builder.ToImmutable();
            }

            if (changedSegments != null)
            {
                // Scan the post-merge flags so a flag arriving in this same call is covered.
                foreach (var entry in newFlags)
                {
                    if (ReferencesAnySegment(entry.Value, changedSegments))
                    {
                        (affected ??= new HashSet<string>(StringComparer.Ordinal)).Add(entry.Key);
                    }
                }
            }

            // SSE `flag.created`/`flag.updated` land here with a single flag, so this is
            // the primary real-time path on which a toggled prerequisite must still reach
            // its dependents. Runs last, over the fully-resolved affected set.
            if (affected != null)
            {
                AddPrerequisiteDependents(newFlags, affected);
            }

            // Deliberately does NOT set _hasSnapshot: only a full Replace establishes the
            // baseline (matching the JS reference, where init sets hasSnapshot and upsert
            // does not). An SSE delta can arrive before the initial load completes — if it
            // promoted a one-flag store to "baseline", the first real Replace would then
            // diff the full snapshot against that single flag and report every other flag
            // as added, i.e. raise the cold load as a change.
            _snapshot = new FlagSnapshot(newFlags, newSegments);
            changed = affected;
        }

        Notify(changed);
    }

    /// <summary>
    /// Atomically removes a flag by key.
    /// </summary>
    public void RemoveFlag(string key)
    {
        HashSet<string>? affected = null;

        lock (_updateLock)
        {
            var current = _snapshot;
            if (current.Flags.ContainsKey(key))
            {
                // Dependents of the deleted flag flip to `prerequisite-failed`, so they
                // change value too and must be reported alongside it.
                var remaining = current.Flags.Remove(key);
                affected = new HashSet<string>(StringComparer.Ordinal) { key };
                AddPrerequisiteDependents(remaining, affected);
                _snapshot = new FlagSnapshot(remaining, current.Segments);
            }
        }

        Notify(affected);
    }

    /// <summary>
    /// Atomically removes a segment by key. No production caller today (there is no
    /// <c>segment.deleted</c> SSE event), but it notifies the flags whose rules targeted
    /// the segment so wiring one later cannot silently serve stale evaluations.
    /// </summary>
    public void RemoveSegment(string key)
    {
        IReadOnlyCollection<string>? affected = null;

        lock (_updateLock)
        {
            var current = _snapshot;
            if (current.Segments.ContainsKey(key))
            {
                // Fan out through prerequisites as well, so a flag gated on one of the
                // segment's referencing flags is not left serving a stale value.
                var referencing = FlagsReferencingSegment(current, key);
                if (referencing != null)
                {
                    AddPrerequisiteDependents(current.Flags, referencing);
                }
                affected = referencing;
                _snapshot = new FlagSnapshot(current.Flags, current.Segments.Remove(key));
            }
        }

        Notify(affected);
    }

    /// <summary>
    /// Registers a listener for flag-configuration changes. Dispose the returned
    /// handle to unsubscribe; disposing twice is a no-op.
    /// </summary>
    public IDisposable OnChange(FlagChangeListener listener)
    {
        if (listener == null) throw new ArgumentNullException(nameof(listener));

        lock (_listenerLock)
        {
            var current = _listeners;
            var next = new FlagChangeListener[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = listener;
            _listeners = next;
        }

        return new Subscription(this, listener);
    }

    private void Unsubscribe(FlagChangeListener listener)
    {
        lock (_listenerLock)
        {
            var current = _listeners;
            var index = Array.IndexOf(current, listener);
            if (index < 0) return;

            if (current.Length == 1)
            {
                _listeners = Array.Empty<FlagChangeListener>();
                return;
            }

            var next = new FlagChangeListener[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            _listeners = next;
        }
    }

    /// <summary>
    /// Fires the registered listeners. MUST be called after releasing
    /// <c>_updateLock</c>: a listener that calls back into the store while the lock is
    /// held would deadlock. A throwing listener is isolated so it neither skips the
    /// remaining listeners nor escapes into the polling/SSE loop that triggered the
    /// update; the fan-out layer above logs it.
    /// </summary>
    private void Notify(IReadOnlyCollection<string>? keys)
    {
        if (keys == null || keys.Count == 0) return;

        var listeners = _listeners;
        foreach (var listener in listeners)
        {
            try
            {
                listener(keys);
            }
            catch
            {
                // Swallowed here; SharedFeatureflipCore's fan-out logs its own listeners.
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private FlagStore? _store;
        private readonly FlagChangeListener _listener;

        public Subscription(FlagStore store, FlagChangeListener listener)
        {
            _store = store;
            _listener = listener;
        }

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            store?.Unsubscribe(_listener);
        }
    }

    public bool TryGetFlag(string key, out FlagConfiguration? flag)
        => _snapshot.Flags.TryGetValue(key, out flag);

    public bool TryGetSegment(string key, out Segment? segment)
        => _snapshot.Segments.TryGetValue(key, out segment);

    public IReadOnlyDictionary<string, FlagConfiguration> GetAllFlags() => _snapshot.Flags;
    public IReadOnlyDictionary<string, Segment> GetAllSegments() => _snapshot.Segments;

    /// <summary>
    /// Keys of the flags affected by replacing <paramref name="current"/> with the given
    /// dictionaries. A flag counts as affected when it is added, removed, or its
    /// <c>Version</c> changed — and also when one of its rules targets a segment that was
    /// itself added, removed, or version-bumped, since a segment edit alters evaluation
    /// outcomes without bumping the referencing flag's own version. Only the incoming
    /// (<paramref name="nextFlags"/>) flags need scanning for that segment case: a flag
    /// that dropped its reference to a changed segment must have had its own rules edited,
    /// which bumps its version and is already caught by the first check. Finally, the set is
    /// expanded through prerequisites (see <see cref="AddPrerequisiteDependents"/>), which
    /// alter a dependent's evaluated value without bumping its version either. Must be
    /// called while holding <c>_updateLock</c>.
    /// </summary>
    private static IReadOnlyCollection<string>? DiffSnapshot(
        FlagSnapshot current,
        ImmutableDictionary<string, FlagConfiguration> nextFlags,
        ImmutableDictionary<string, Segment> nextSegments)
    {
        HashSet<string>? changed = null;

        foreach (var entry in nextFlags)
        {
            if (!current.Flags.TryGetValue(entry.Key, out var previous) || Differs(previous, entry.Value))
            {
                (changed ??= new HashSet<string>(StringComparer.Ordinal)).Add(entry.Key);
            }
        }

        foreach (var key in current.Flags.Keys)
        {
            if (!nextFlags.ContainsKey(key))
            {
                (changed ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
            }
        }

        HashSet<string>? changedSegments = null;

        foreach (var entry in nextSegments)
        {
            if (!current.Segments.TryGetValue(entry.Key, out var previous) || Differs(previous, entry.Value))
            {
                (changedSegments ??= new HashSet<string>(StringComparer.Ordinal)).Add(entry.Key);
            }
        }

        foreach (var key in current.Segments.Keys)
        {
            if (!nextSegments.ContainsKey(key))
            {
                (changedSegments ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
            }
        }

        if (changedSegments != null)
        {
            // Only the incoming flags need scanning: a flag that dropped a reference to a
            // changed segment must have had its own rules edited, which changes its own
            // config and is already reported above.
            foreach (var entry in nextFlags)
            {
                if (ReferencesAnySegment(entry.Value, changedSegments))
                {
                    (changed ??= new HashSet<string>(StringComparer.Ordinal)).Add(entry.Key);
                }
            }
        }

        // Must run last: it walks out from the fully-resolved changed set, so a flag
        // that only the segment scan above pulled in still reaches its dependents.
        if (changed != null)
        {
            AddPrerequisiteDependents(nextFlags, changed);
        }

        return changed;
    }

    /// <summary>
    /// True when a full snapshot's configuration for one key differs from the stored one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compares configuration content, not just <c>Version</c>. The wire version is
    /// deliberately second-granular — <c>FlagDtoMapper.ToSdkVersion</c> divides the
    /// internal epoch-millisecond version by 1000 because published SDKs declare 32-bit
    /// version fields — so it is not a sufficient change signal. Two edits to one flag
    /// inside the same wall-clock second carry an identical version, and if a snapshot
    /// boundary falls between them the later config lands in the store while comparing
    /// versions reports nothing changed, leaving a consumer that caches per key stale
    /// until the next unrelated edit (#2088).
    /// </para>
    /// <para>
    /// Serializing is used rather than a hand-written field-by-field comparison so a
    /// field added to a config model is covered automatically; a comparison that had to
    /// be extended by hand would silently under-report the day someone forgot. Record
    /// equality is not an option: the generated <c>Equals</c> compares the
    /// <c>List&lt;T&gt;</c> properties by reference, so two structurally identical
    /// snapshots would compare unequal and every poll would report every flag.
    /// </para>
    /// <para>
    /// Cost is bounded by one serialization pass over a snapshot the caller just
    /// deserialized off the wire, so it adds at most as much work as the parse already
    /// spent — and only on full snapshots (poll, <c>sync</c>, <c>segment.updated</c>),
    /// never per evaluation. No version fast path: an unchanged flag has a matching version,
    /// so a version pre-check would skip serialization only for the flags that did change —
    /// the rare case — while the common one still has to be compared.
    /// </para>
    /// </remarks>
    private static bool Differs<T>(T previous, T next)
        => !JsonSerializer.SerializeToUtf8Bytes(previous, ComparisonOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(next, ComparisonOptions));

    /// <summary>
    /// Serializer options for <see cref="Differs{T}"/> only — never for wire traffic.
    /// </summary>
    private static readonly JsonSerializerOptions ComparisonOptions = new()
    {
        Converters = { new UndefinedTolerantJsonElementConverter() }
    };

    /// <summary>
    /// Writes an <see cref="JsonValueKind.Undefined"/> element as JSON null instead of
    /// throwing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Variation.Value</c> is a non-nullable <see cref="JsonElement"/>, and
    /// System.Text.Json leaves it at <c>default</c> — <c>ValueKind.Undefined</c> — for
    /// <em>both</em> an absent <c>value</c> property and an explicit <c>"value": null</c>.
    /// A variation legitimately holding JSON null is a supported case, so this is ordinary
    /// config, not a malformed payload. <c>JsonElement.WriteTo</c> throws
    /// <see cref="InvalidOperationException"/> on <c>Undefined</c>, and the throw would
    /// escape <see cref="DiffSnapshot"/> before the snapshot is swapped in — so a single
    /// such variation would make every full snapshot fail and freeze the store on its last
    /// good config indefinitely, which is far worse than the missed notification the diff
    /// exists to fix. These models were only ever deserialized before, never re-serialized,
    /// which is why the hazard is new here.
    /// </para>
    /// <para>
    /// Collapsing <c>Undefined</c> onto null makes the two indistinguishable to the
    /// comparison. That is the correct reading rather than a compromise: the wire cannot
    /// express the difference in the first place, since an explicit null deserializes to
    /// <c>Undefined</c> too.
    /// </para>
    /// </remarks>
    private sealed class UndefinedTolerantJsonElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonElement.ParseValue(ref reader);

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
                return;
            }

            value.WriteTo(writer);
        }
    }

    /// <summary>
    /// Expands <paramref name="changed"/> in place with every flag that transitively
    /// depends on an already-changed flag through a prerequisite.
    /// </summary>
    /// <remarks>
    /// A flag's <c>Version</c> covers its own prerequisite rows but not the flags those
    /// rows point at, so toggling a prerequisite bumps only the prerequisite's version.
    /// <c>FlagEvaluator</c> resolves prerequisites recursively, so the dependent's value
    /// still flips (to its off variation, <c>prerequisite-failed</c>) — leaving the
    /// dependent silently absent from the change notification.
    /// <para>
    /// Walks reverse edges out from the changed set rather than scanning every flag's
    /// chain, so cost is proportional to the affected subgraph rather than the whole
    /// snapshot. Over-reporting is harmless (a listener re-reads a flag that evaluates
    /// the same); under-reporting is the defect this closes.
    /// </para>
    /// </remarks>
    private static void AddPrerequisiteDependents(
        ImmutableDictionary<string, FlagConfiguration> flags,
        HashSet<string> changed)
    {
        if (changed.Count == 0) return;

        // Prerequisite key -> flags naming it. Built from the post-mutation flags, so a
        // *removed* flag still resolves its dependents: they are still present and still
        // carry the now-dangling prerequisite row.
        Dictionary<string, List<string>>? dependents = null;

        foreach (var entry in flags)
        {
            // Null-tolerant for the same reason as ReferencesAnySegment: an explicit
            // `"prerequisites": null` overrides the property initializer, and a malformed
            // payload must not abort the update and leave the store permanently stale.
            var prerequisites = entry.Value.Prerequisites;
            if (prerequisites == null) continue;

            foreach (var prerequisite in prerequisites)
            {
                var key = prerequisite?.PrerequisiteFlagKey;
                if (string.IsNullOrEmpty(key)) continue;

                dependents ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!dependents.TryGetValue(key!, out var list))
                {
                    list = new List<string>();
                    dependents[key!] = list;
                }

                list.Add(entry.Key);
            }
        }

        if (dependents == null) return;

        // `changed` doubles as the visited set — HashSet.Add reports whether the key was
        // new — so a prerequisite cycle (rejected by the server, but reachable via a
        // malformed payload) terminates instead of looping forever. No depth cap is
        // needed for the same reason: unlike the evaluator, each flag is visited once.
        var pending = new Stack<string>(changed);
        while (pending.Count > 0)
        {
            if (!dependents.TryGetValue(pending.Pop(), out var list)) continue;

            foreach (var dependent in list)
            {
                if (changed.Add(dependent))
                {
                    pending.Push(dependent);
                }
            }
        }
    }

    /// <summary>
    /// Whether any of the flag's rules targets one of the given segment keys. Tolerates a
    /// null <c>Rules</c> collection: a malformed payload must not abort a snapshot update
    /// and leave the store permanently stale.
    /// </summary>
    private static bool ReferencesAnySegment(FlagConfiguration flag, ICollection<string> segmentKeys)
    {
        var rules = flag.Rules;
        if (rules == null) return false;

        foreach (var rule in rules)
        {
            if (rule?.SegmentKey != null && segmentKeys.Contains(rule.SegmentKey))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keys of the flags in <paramref name="snapshot"/> whose rules target
    /// <paramref name="segmentKey"/>.
    /// </summary>
    private static HashSet<string>? FlagsReferencingSegment(FlagSnapshot snapshot, string segmentKey)
    {
        var lookup = new[] { segmentKey };
        HashSet<string>? affected = null;

        foreach (var entry in snapshot.Flags)
        {
            if (ReferencesAnySegment(entry.Value, lookup))
            {
                (affected ??= new HashSet<string>(StringComparer.Ordinal)).Add(entry.Key);
            }
        }

        return affected;
    }

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
