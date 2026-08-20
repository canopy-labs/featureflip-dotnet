using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Featureflip.Client;

/// <summary>
/// The main client for evaluating feature flags. Obtain instances via the static
/// factory <see cref="Get(string?, FeatureFlagOptions?, ILogger{FeatureflipClient}?)"/>
/// — direct instantiation is not supported. Multiple <c>Get</c> calls with the same
/// SDK key return handles sharing one underlying client (refcounted); the shared core
/// shuts down when the last handle is disposed.
/// </summary>
public sealed class FeatureflipClient : IFeatureflipClient
{
    private const string SdkKeyEnvVar = "FEATUREFLIP_SDK_KEY";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SharedFeatureflipCore> LiveCores =
        new(StringComparer.Ordinal);

    private readonly SharedFeatureflipCore _core;
    private int _disposed; // per-handle disposal flag; 0 = alive, 1 = disposed

    // One core subscription per registered handler. Guarded by _flagsChangedLock.
    private readonly object _flagsChangedLock = new();
    private Dictionary<EventHandler<FlagsChangedEventArgs>, IDisposable>? _flagsChangedSubscriptions;

    /// <summary>
    /// Internal constructor retained for cases where a caller needs a standalone,
    /// non-cached client (e.g., specialized testing or embedding scenarios).
    /// The primary public entry point is <see cref="Get(string?, FeatureFlagOptions?, ILogger{FeatureflipClient}?)"/>.
    /// </summary>
    /// <param name="sdkKey">The SDK key for authentication. If null, reads from FEATUREFLIP_SDK_KEY environment variable.</param>
    /// <param name="options">Configuration options. If null, uses defaults.</param>
    /// <param name="logger">Optional logger instance.</param>
    /// <exception cref="FeatureFlagInitializationException">Thrown when initialization fails and WaitForInitialization is true.</exception>
    internal FeatureflipClient(
        string? sdkKey = null,
        FeatureFlagOptions? options = null,
        ILogger<FeatureflipClient>? logger = null)
    {
        sdkKey ??= Environment.GetEnvironmentVariable(SdkKeyEnvVar);
        if (string.IsNullOrWhiteSpace(sdkKey))
        {
            throw new FeatureFlagInitializationException(
                $"SDK key is required. Provide it as a parameter or set the {SdkKeyEnvVar} environment variable.");
        }

        _core = new SharedFeatureflipCore(
            sdkKey,
            options ?? new FeatureFlagOptions(),
            (ILogger?)logger ?? NullLogger.Instance);
    }

    /// <summary>
    /// Internal constructor for testing. Creates a handle backed by a standalone
    /// test-only shared core that bypasses HTTP and background tasks.
    /// </summary>
    internal FeatureflipClient(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options)
    {
        _core = SharedFeatureflipCore.CreateForTesting(store, evaluator, options);
    }

    /// <summary>
    /// Internal constructor used by the static factory.
    /// </summary>
    /// <remarks>
    /// REFCOUNT CONTRACT: the caller must guarantee that <paramref name="core"/> already
    /// has a refcount increment reserved for this handle — either because the core was
    /// just constructed (which sets refcount to 1 for the first handle) or because the
    /// caller successfully invoked <see cref="SharedFeatureflipCore.TryAcquire"/> before
    /// passing it here. This constructor does NOT call TryAcquire itself, to avoid
    /// double-counting on the fresh-core path. The handle's <see cref="Dispose"/> will
    /// call <see cref="SharedFeatureflipCore.Release"/> exactly once.
    /// </remarks>
    internal FeatureflipClient(SharedFeatureflipCore core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    /// <summary>
    /// Returns a client for the given SDK key. The first call with a given key constructs
    /// and initializes a shared core; subsequent calls with the same key return a new handle
    /// pointing at the cached core. When the last handle for a key is disposed, the core
    /// shuts down and is removed from the cache.
    /// </summary>
    /// <param name="sdkKey">
    /// The SDK key for authentication. If null, reads from the FEATUREFLIP_SDK_KEY environment variable.
    /// </param>
    /// <param name="options">
    /// Configuration options. Honored only on the first call for a given SDK key. If a later call
    /// passes meaningfully different options, a warning is logged and the cached instance is returned.
    /// </param>
    /// <param name="logger">Optional logger instance.</param>
    public static IFeatureflipClient Get(
        string? sdkKey = null,
        FeatureFlagOptions? options = null,
        ILogger<FeatureflipClient>? logger = null)
    {
        sdkKey ??= Environment.GetEnvironmentVariable(SdkKeyEnvVar);
        if (string.IsNullOrWhiteSpace(sdkKey))
        {
            throw new FeatureFlagInitializationException(
                $"SDK key is required. Provide it as a parameter or set the {SdkKeyEnvVar} environment variable.");
        }

        var resolvedOptions = options ?? new FeatureFlagOptions();
        var resolvedLogger = (ILogger?)logger ?? NullLogger.Instance;

        // Retry loop handles the race where a cached core is found but has already begun
        // shutting down (refcount hit 0 between lookup and TryAcquire). Progress is
        // guaranteed on every iteration: we either acquire a live core and return, clean
        // up a stale entry and retry (map shrinks), successfully add a new core and return,
        // or lose a TryAdd race and retry against the winner (which is now live in the map).
        while (true)
        {
            if (LiveCores.TryGetValue(sdkKey, out var existingCore))
            {
                if (existingCore.TryAcquire())
                {
                    if (!OptionsEqual(existingCore.Options, resolvedOptions))
                    {
                        resolvedLogger.LogWarning(
                            "FeatureflipClient.Get called with different options for SDK key already in use. " +
                            "The cached instance's options are preserved; the passed options are ignored.");
                    }
                    return new FeatureflipClient(existingCore);
                }

                // Stale entry — core shut down between lookup and acquire. Remove and retry.
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, SharedFeatureflipCore>>)LiveCores)
                    .Remove(new System.Collections.Generic.KeyValuePair<string, SharedFeatureflipCore>(sdkKey, existingCore));
                continue;
            }

            var newCore = new SharedFeatureflipCore(sdkKey, resolvedOptions, resolvedLogger);
            if (LiveCores.TryAdd(sdkKey, newCore))
            {
                newCore.SetOwningMap(LiveCores, sdkKey);
                return new FeatureflipClient(newCore);
            }

            // Another thread added one concurrently — release our speculative core and retry.
            newCore.Release();
        }
    }

    /// <summary>Diagnostic: current number of live shared cores in the static map. Test-only.</summary>
    internal static int DebugLiveCoreCount => LiveCores.Count;

    /// <summary>
    /// Resets the static core map. For test isolation only. Forces shutdown of each
    /// live core's map-held reference; any handles still outstanding will continue to
    /// function on their own references until they are disposed.
    /// </summary>
    internal static void ResetForTesting()
    {
        var col = (System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, SharedFeatureflipCore>>)LiveCores;
        foreach (var kvp in LiveCores.ToArray())
        {
            if (col.Remove(kvp))
            {
                kvp.Value.ForceShutdownFromReset();
            }
        }
    }

    // NOTE: FeatureFlagOptions.Inspectors is deliberately excluded — delegates are not
    // structurally comparable, and a differing callback must not trigger the spurious
    // "different options" warning. Like every other option, inspectors are honored on
    // the first Get() for a given SDK key.
    private static bool OptionsEqual(FeatureFlagOptions a, FeatureFlagOptions b)
    {
        return a.BaseUrl == b.BaseUrl
            && a.Streaming == b.Streaming
            && a.PollInterval == b.PollInterval
            && a.FlushInterval == b.FlushInterval
            && a.FlushBatchSize == b.FlushBatchSize
            && a.InitTimeout == b.InitTimeout
            && a.WaitForInitialization == b.WaitForInitialization
            && a.StartOffline == b.StartOffline
            && a.ConnectTimeout == b.ConnectTimeout
            && a.ReadTimeout == b.ReadTimeout;
    }

    /// <inheritdoc />
    public event EventHandler<FlagsChangedEventArgs> FlagsChanged
    {
        add
        {
            if (value == null) return;

            lock (_flagsChangedLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;

                _flagsChangedSubscriptions ??= new Dictionary<EventHandler<FlagsChangedEventArgs>, IDisposable>();
                if (_flagsChangedSubscriptions.ContainsKey(value)) return;

                var handler = value;
                _flagsChangedSubscriptions[value] = _core.OnFlagsChanged(keys =>
                {
                    // Defense against an in-flight fan-out: SharedFeatureflipCore.NotifyFlagsChanged
                    // snapshots its listener array before invoking, so a notification already under
                    // way when this handle's Dispose runs can still reach this closure even after the
                    // subscription has been (or is concurrently being) torn down. The spec promises
                    // "never after the handle is disposed", absolutely, so guard here rather than
                    // relying on removal timing alone.
                    if (Volatile.Read(ref _disposed) != 0) return;

                    // Copy per handler: `keys` is the single instance threaded through every
                    // listener registered for this update (SharedFeatureflipCore.NotifyFlagsChanged
                    // iterates one shared array), and its runtime type (HashSet<string> from
                    // FlagStore.Replace/Upsert, or string[] from FlagStore.RemoveFlag) is castable
                    // back out of IReadOnlyCollection<string> and mutable. Without a copy, one
                    // handler downcasting and mutating it would corrupt what every other handler —
                    // and every other handle sharing this core — observes for the same update.
                    // Mirrors the JS reference's per-listener copy (packages/js-sdk/src/core/shared-core.ts:
                    // listener([...keys])) — do not optimize this copy away.
                    handler(this, new FlagsChangedEventArgs(new List<string>(keys)));
                });
            }
        }
        remove
        {
            if (value == null) return;

            IDisposable? subscription = null;
            lock (_flagsChangedLock)
            {
                if (_flagsChangedSubscriptions != null
                    && _flagsChangedSubscriptions.TryGetValue(value, out subscription))
                {
                    _flagsChangedSubscriptions.Remove(value);
                }
            }

            // Disposed outside the lock: unsubscribing reaches into the core's own
            // listener lock, and taking the two in a fixed order everywhere avoids
            // building a lock cycle.
            subscription?.Dispose();
        }
    }

    /// <inheritdoc />
    public bool IsInitialized => Volatile.Read(ref _disposed) == 0 && _core.IsInitialized;

    /// <inheritdoc />
    public Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
        => _core.WaitForInitializationAsync(cancellationToken);

    /// <inheritdoc />
    public T Variation<T>(string key, EvaluationContext context, T defaultValue)
    {
        // A disposed handle serves the caller's default (#2309). Dispose releases
        // the core — stopping streaming/polling and shutting down the event
        // processor — but the in-memory store stays readable, so without this
        // guard the handle would keep serving a frozen snapshot that can never
        // update again. Returning early also suppresses the evaluation event a
        // disposed client must not emit.
        if (Volatile.Read(ref _disposed) != 0) return defaultValue;

        var detail = _core.Evaluate<T>(key, context, defaultValue);
        _core.TrackEvaluation(key, context, detail);
        return detail.Value;
    }

    /// <inheritdoc />
    public EvaluationDetail<T> VariationDetail<T>(string key, EvaluationContext context, T defaultValue)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new EvaluationDetail<T>(defaultValue, EvaluationReason.Error, null, "Client is disposed");
        }

        var detail = _core.Evaluate<T>(key, context, defaultValue);
        _core.TrackEvaluation(key, context, detail);
        return detail;
    }

    /// <inheritdoc />
    public bool BoolVariation(string key, EvaluationContext context, bool defaultValue)
        => Variation(key, context, defaultValue);

    /// <inheritdoc />
    public string StringVariation(string key, EvaluationContext context, string defaultValue)
        => Variation(key, context, defaultValue);

    /// <inheritdoc />
    public int IntVariation(string key, EvaluationContext context, int defaultValue)
        => Variation(key, context, defaultValue);

    /// <inheritdoc />
    public double DoubleVariation(string key, EvaluationContext context, double defaultValue)
        => Variation(key, context, defaultValue);

    /// <inheritdoc />
    public T JsonVariation<T>(string key, EvaluationContext context, T defaultValue)
        => Variation(key, context, defaultValue);

    /// <inheritdoc />
    public void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _core.Flush();
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        return _core.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Dictionary<EventHandler<FlagsChangedEventArgs>, IDisposable>? subscriptions;
        lock (_flagsChangedLock)
        {
            subscriptions = _flagsChangedSubscriptions;
            _flagsChangedSubscriptions = null;
        }

        if (subscriptions != null)
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Dispose();
            }
        }

        _core.Release();
    }
}
