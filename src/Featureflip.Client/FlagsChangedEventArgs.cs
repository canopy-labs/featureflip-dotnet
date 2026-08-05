namespace Featureflip.Client;

/// <summary>
/// Payload for <see cref="IFeatureflipClient.FlagsChanged"/>: the keys of the flags whose
/// configuration changed in a single update.
/// </summary>
/// <remarks>
/// Batched — one event per update carrying every affected key, so a poll that changes 40
/// flags raises once. Never raised with an empty collection, and never for the initial
/// flag load (a cold load is not a configuration change; use
/// <see cref="IFeatureflipClient.WaitForInitializationAsync"/> for that).
/// </remarks>
public sealed class FlagsChangedEventArgs : EventArgs
{
    /// <summary>
    /// Creates the event payload.
    /// </summary>
    /// <param name="changedKeys">The affected flag keys. Unordered and duplicate-free.</param>
    public FlagsChangedEventArgs(IReadOnlyCollection<string> changedKeys)
    {
        ChangedKeys = changedKeys ?? throw new ArgumentNullException(nameof(changedKeys));
    }

    /// <summary>
    /// Keys of the flags affected by this update. Unordered and duplicate-free — do not
    /// depend on ordering. A key appears when the flag was added, removed, its
    /// configuration changed, a segment its rules reference changed, or a flag it lists as
    /// a prerequisite changed (resolved transitively).
    /// </summary>
    /// <remarks>
    /// Change detection on a full snapshot compares configuration content rather than the
    /// wire <c>Version</c>, which is second-granular and so cannot by itself distinguish two
    /// edits to one flag inside the same wall-clock second; SSE deltas raise
    /// unconditionally, since the server sends one only for a real change. What remains is
    /// ordinary snapshot coalescing, and it is not specific to any time window: edits that
    /// land between two full snapshots are reported once, as the net change, and edits that
    /// cancel out before the next snapshot report nothing — correctly, because the stored
    /// configuration is then the one already in effect.
    /// </remarks>
    public IReadOnlyCollection<string> ChangedKeys { get; }
}
