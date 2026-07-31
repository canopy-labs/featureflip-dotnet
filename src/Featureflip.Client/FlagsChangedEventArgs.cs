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
    /// configuration version changed, a segment its rules reference changed, or a flag it
    /// lists as a prerequisite changed (resolved transitively).
    /// </summary>
    /// <remarks>
    /// One known gap, shared with the other Featureflip SDKs: the configuration version is
    /// second-granular on the wire, so two edits to one flag within the same second can
    /// produce a single notification. The store still applies the newer configuration —
    /// only the notification is coalesced.
    /// </remarks>
    public IReadOnlyCollection<string> ChangedKeys { get; }
}
