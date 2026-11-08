namespace Featureflip.Client;

/// <summary>Where a client's flag data came from at startup.</summary>
public enum StartupSource
{
    /// <summary>There is no data yet, so caller defaults are served. The zero value, so <c>default(StartupSource)</c> is the safe reading.</summary>
    None = 0,

    /// <summary>The client started from the snapshot store.</summary>
    Snapshot,

    /// <summary>A successful fetch from the Evaluation API.</summary>
    Live
}

/// <summary>
/// Immutable description of where the client's flag data came from. Read it from
/// <see cref="IFeatureflipClient.StartupInfo"/>.
/// </summary>
public sealed class StartupInfo
{
    /// <summary>The reason reported for a client that started from a stored snapshot.</summary>
    public const string ReasonSnapshot = "snapshot";

    /// <summary>The reason reported when a stored snapshot was older than the maximum age.</summary>
    public const string ReasonSnapshotExpired = "snapshot-expired";

    /// <summary>The reason reported when stored text failed a validity check.</summary>
    public const string ReasonSnapshotInvalid = "snapshot-invalid";

    /// <summary>Creates a startup description.</summary>
    public StartupInfo(StartupSource source, string? reason = null, long? snapshotAgeSeconds = null)
    {
        Source = source;
        Reason = reason;
        SnapshotAgeSeconds = snapshotAgeSeconds;
    }

    /// <summary>
    /// <see cref="StartupSource.Live"/> after a successful fetch, <see cref="StartupSource.Snapshot"/>
    /// after a start from the snapshot store, <see cref="StartupSource.None"/> when no data is loaded.
    /// </summary>
    public StartupSource Source { get; }

    /// <summary>
    /// <c>"snapshot"</c> for a snapshot start, <c>"snapshot-expired"</c> or <c>"snapshot-invalid"</c>
    /// when a stored snapshot was ignored. <c>null</c> once the client is live, or when nothing was stored.
    /// </summary>
    public string? Reason { get; }

    /// <summary>Age of the snapshot that was used or rejected as expired, in whole seconds.</summary>
    public long? SnapshotAgeSeconds { get; }

    internal static StartupInfo Live { get; } = new(StartupSource.Live);

    internal static StartupInfo None { get; } = new(StartupSource.None);

    /// <inheritdoc />
    public override string ToString()
        => $"{Source}" + (Reason != null ? $" ({Reason})" : string.Empty)
           + (SnapshotAgeSeconds != null ? $", {SnapshotAgeSeconds}s old" : string.Empty);
}
