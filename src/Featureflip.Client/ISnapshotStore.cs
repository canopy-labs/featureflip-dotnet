namespace Featureflip.Client;

/// <summary>
/// Pluggable persistence for the last good flag configuration, an opaque text store.
/// The SDK owns the stored text end to end (envelope, environment check, age check and
/// every startup reason), so a store only has to hand back exactly what it was last given.
/// One store instance may safely be given to several SDK keys: the SDK rejects text that
/// was written for a different SDK key or base URL.
/// </summary>
/// <remarks>
/// Both methods may be called from a background thread. An exception (or a faulted task)
/// from either method is handled by the SDK: a failing <see cref="GetAsync"/> is treated as
/// "no snapshot" and a failing <see cref="PutAsync"/> as a dropped write, each with a logged
/// warning. The token passed to <see cref="GetAsync"/> is cancelled when the initial read
/// exceeds <see cref="FeatureFlagOptions.InitTimeout"/>, but a store that ignores it is
/// still bounded: the SDK stops waiting and ignores a late result.
/// </remarks>
public interface ISnapshotStore
{
    /// <summary>Returns the text last stored, or <c>null</c> when nothing is stored.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Stores the text, replacing any previous value.</summary>
    Task PutAsync(string text, CancellationToken cancellationToken);
}
