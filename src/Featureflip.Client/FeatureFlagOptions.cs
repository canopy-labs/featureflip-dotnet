namespace Featureflip.Client;

/// <summary>
/// Configuration options for the feature flag client.
/// </summary>
public class FeatureFlagOptions
{
    /// <summary>Base URL of the Evaluation API.</summary>
    public string BaseUrl { get; set; } = "https://eval.featureflip.io";

    /// <summary>Timeout for establishing connections.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout for reading responses.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to use SSE streaming for real-time updates. If false, uses polling.</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Interval between polling requests when streaming is disabled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval between flushing evaluation events to the server.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of events to batch before flushing.</summary>
    public int FlushBatchSize { get; set; } = 100;

    /// <summary>Timeout for initial flag data fetch during initialization.</summary>
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to block the constructor until flags are loaded.
    /// If false, the client starts immediately and loads flags in the background.
    /// </summary>
    public bool WaitForInitialization { get; set; } = false;

    /// <summary>
    /// Whether to start the client even if initial flag loading fails.
    /// If true, the client will return default values until flags are successfully loaded.
    /// Default is false to fail fast on configuration errors.
    /// </summary>
    public bool StartOffline { get; set; } = false;

    /// <summary>
    /// In-process observers invoked synchronously on every flag evaluation, on every
    /// exit path (success, flag-not-found, error). Use these to pipe exposures into
    /// your own analytics tooling.
    /// </summary>
    /// <remarks>
    /// Honored on the <b>first</b> <see cref="FeatureflipClient.Get"/> call for a given
    /// SDK key, like every other option — the shared core snapshots (and copies) this
    /// collection at construction, so later mutations are ignored and the read path
    /// needs no locking. Null entries are dropped defensively. Deliberately excluded
    /// from the "different options" equality check: delegates are not structurally
    /// comparable, and a differing callback must not trigger a spurious warning.
    /// </remarks>
    public IList<EvaluationInspector> Inspectors { get; set; } = new List<EvaluationInspector>();

    /// <summary>
    /// Name of a library that wraps this SDK, such as an OpenFeature provider. Sent after the
    /// SDK's own token in the User-Agent (<c>featureflip-csharp/&lt;version&gt; my-wrapper/1.0.0</c>) so
    /// usage through the wrapper can be counted. Must be a valid HTTP product token; an invalid
    /// one is ignored with a warning. Leave unset when calling the SDK directly.
    /// </summary>
    /// <remarks>
    /// Excluded from the "different options" equality check: it identifies the caller rather
    /// than changing behavior, and a wrapper sharing an SDK key with the host application would
    /// otherwise warn on every construction. Honored on the first <see cref="FeatureflipClient.Get"/>
    /// call for a given SDK key, like every other option.
    /// </remarks>
    public string? WrapperName { get; set; }

    /// <summary>Version of the wrapper named by <see cref="WrapperName"/>. Optional.</summary>
    public string? WrapperVersion { get; set; }
}
