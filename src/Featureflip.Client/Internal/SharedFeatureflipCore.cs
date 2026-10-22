using System.Globalization;
using System.Text.Json;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Featureflip.Client.Internal;

/// <summary>
/// Internal shared core owning all expensive resources of a FeatureflipClient.
/// Refcounted: multiple FeatureflipClient handles can share one core, and the
/// real shutdown runs only when the last handle is disposed.
/// </summary>
internal sealed class SharedFeatureflipCore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // SSE reconnect backoff (mirrors go/python: 3s base, 30s cap). Applied on
    // every reconnect — including a clean/immediate EOF — so a persistently
    // EOF-ing stream cannot busy-loop the reconnect (GAP 6).
    private static readonly TimeSpan SseReconnectBaseDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SseReconnectMaxDelay = TimeSpan.FromSeconds(30);

    // Liveness watchdog: reconnect if no bytes (not even the ~30s server ping)
    // arrive within this window (≈3 missed pings). Without it a half-open socket
    // (silent LB/NAT idle-drop, no FIN/RST) would block the reader forever on the
    // infinite stream read timeout and never reconnect/fall back. Reset on every
    // received event. (On netstandard2.0 the between-reads reset can't interrupt
    // a blocked ReadLineAsync — no cancellable overload — so there the watchdog
    // only bounds the connect phase.)
    private static readonly TimeSpan SseIdleTimeout = TimeSpan.FromSeconds(90);

    private readonly FlagStore _store;
    private readonly FlagEvaluator _evaluator;
    private readonly EventProcessor _eventProcessor;
    private readonly FeatureFlagOptions _options;
    private readonly ILogger _logger;

    // Snapshot of the configured evaluation inspectors, taken once at construction and
    // never mutated afterwards. Immutable-after-init means the hot evaluation path can
    // read it without any locking (see the design spec's rationale for rejecting
    // runtime registration). Empty array when none are configured.
    private readonly EvaluationInspector[] _inspectors;

    // Per-handle flag-change listeners. Copy-on-write under _flagsChangedLock so the
    // notification path reads one volatile field and iterates without locking.
    private volatile Action<IReadOnlyCollection<string>>[] _flagsChangedListeners =
        Array.Empty<Action<IReadOnlyCollection<string>>>();
    private readonly object _flagsChangedLock = new();
    private readonly IDisposable _storeSubscription;

    private readonly FeatureFlagHttpClient? _httpClient;
    private readonly CancellationTokenSource _cts;
    private readonly Task? _flushTask;
    private readonly Task? _refreshTask;
    private readonly TaskCompletionSource<bool> _initializationTcs;

    private int _refCount;
    private int _isShutDown;

    /// <summary>
    /// UTC ticks before which the batch-size trigger must not start another flush.
    /// </summary>
    /// <remarks>
    /// A re-queued batch leaves the queue at or above <c>FlushBatchSize</c>, so without
    /// this gate every subsequent <see cref="TrackEvaluation{T}"/> would start another
    /// flush — turning a failing endpoint into one request per evaluation, which is worse
    /// for the server than the dropping it replaced. The periodic flush loop is the retry
    /// vehicle; this only suppresses the size trigger between its ticks.
    /// </remarks>
    private long _nextAutoFlushTicks;

    /// <summary>
    /// 1 while a batch-size-triggered flush is in flight, 0 otherwise.
    /// </summary>
    /// <remarks>
    /// The backoff gate alone is not enough: it is only armed once a flush has FAILED, and
    /// the trigger fires again long before the first HTTP round-trip returns. Without this
    /// every evaluation in a tight loop starts its own concurrent flush.
    /// </remarks>
    private int _autoFlushInFlight;

    /// <summary>
    /// Completes when the drain loop currently running finishes; null when none is.
    /// </summary>
    /// <remarks>
    /// <see cref="_autoFlushInFlight"/> above only ever guarded the SIZE trigger.
    /// Nothing stopped the periodic flush loop, an explicit <see cref="FlushAsync"/>
    /// from the public client API and a size-triggered flush from entering the drain
    /// together — two request streams against the endpoint the backoff gate exists to
    /// protect, and a success in one clearing the gate a failure in the other had just
    /// armed, which re-opens the one-request-per-evaluation behaviour outright (#2477).
    /// <para>
    /// Deliberately a task that never faults: a caller coalesced onto an in-flight
    /// drain must not inherit the starter's exception or its cancellation.
    /// </para>
    /// </remarks>
    private Task? _inFlightFlush;
    private readonly object _flushGate = new();
    private volatile bool _isInitialized;

    private System.Collections.Concurrent.ConcurrentDictionary<string, SharedFeatureflipCore>? _owningMap;
    private string? _owningKey;

    public int RefCount => Volatile.Read(ref _refCount);
    public bool IsShutDown => Volatile.Read(ref _isShutDown) != 0;

    /// <summary>Diagnostic: current number of registered flag-change listeners. Test-only.</summary>
    internal int FlagsChangedListenerCount => _flagsChangedListeners.Length;

    public FeatureFlagOptions Options => _options;
    public bool IsInitialized
    {
        get => _isInitialized;
        private set => _isInitialized = value;
    }

    /// <summary>
    /// Real constructor used by FeatureflipClient's primary path and (later) the static factory.
    /// Starts HTTP client and background tasks.
    /// </summary>
    public SharedFeatureflipCore(string sdkKey, FeatureFlagOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(sdkKey)) throw new ArgumentException("SDK key is required.", nameof(sdkKey));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _inspectors = NormalizeInspectors(_options.Inspectors);
        _store = new FlagStore();
        _storeSubscription = _store.OnChange(NotifyFlagsChanged);
        _evaluator = new FlagEvaluator(_logger);
        _eventProcessor = new EventProcessor(_options.FlushInterval, _options.FlushBatchSize);
        _httpClient = new FeatureFlagHttpClient(sdkKey, _options, _logger);
        _cts = new CancellationTokenSource();
        _initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refCount = 1;

        try
        {
            if (_options.WaitForInitialization)
            {
                InitializeSync();
            }
            else
            {
                _logger.LogDebug("Starting background initialization");
                _ = InitializeAsync(_cts.Token);
            }

            _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token), _cts.Token);
            _refreshTask = Task.Run(() => RefreshLoopAsync(_cts.Token), _cts.Token);
        }
        catch (FeatureFlagInitializationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_options.StartOffline)
            {
                _logger.LogWarning(ex, "Initialization failed, starting in offline mode");
                _initializationTcs.TrySetResult(false);
                _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token), _cts.Token);
                _refreshTask = Task.Run(() => RefreshLoopAsync(_cts.Token), _cts.Token);
            }
            else
            {
                throw new FeatureFlagInitializationException("Failed to initialize feature flag client.", ex);
            }
        }
    }

    /// <summary>
    /// Test-only constructor. No HTTP client, no background tasks. Used by unit tests
    /// and by FeatureflipClient's internal test constructor.
    /// </summary>
    private SharedFeatureflipCore(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options, ILogger? logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _storeSubscription = _store.OnChange(NotifyFlagsChanged);
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _eventProcessor = new EventProcessor(options.FlushInterval, options.FlushBatchSize);
        _logger = logger ?? NullLogger.Instance;
        _inspectors = NormalizeInspectors(_options.Inspectors);
        _httpClient = null;
        _cts = new CancellationTokenSource();
        _initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _initializationTcs.TrySetResult(true);
        _flushTask = null;
        _refreshTask = null;
        _refCount = 1;
        _isInitialized = true;
        IsInitialized = true;
    }

    internal static SharedFeatureflipCore CreateForTesting()
    {
        return CreateForTesting(new FlagStore());
    }

    internal static SharedFeatureflipCore CreateForTesting(FlagStore store)
    {
        return new SharedFeatureflipCore(store, new FlagEvaluator(), new FeatureFlagOptions(), logger: null);
    }

    internal static SharedFeatureflipCore CreateForTesting(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options)
    {
        return new SharedFeatureflipCore(store, evaluator, options, logger: null);
    }

    internal static SharedFeatureflipCore CreateForTesting(
        FlagStore store,
        FlagEvaluator evaluator,
        FeatureFlagOptions options,
        ILogger logger)
    {
        return new SharedFeatureflipCore(store, evaluator, options, logger);
    }

    /// <summary>
    /// Called by the factory after successfully inserting this core into the static map.
    /// When the refcount hits zero, Shutdown() will remove the entry via this reference.
    /// </summary>
    public void SetOwningMap(
        System.Collections.Concurrent.ConcurrentDictionary<string, SharedFeatureflipCore> map,
        string key)
    {
        _owningMap = map;
        _owningKey = key;
    }

    /// <summary>
    /// Test-only: called by FeatureflipClient.ResetForTesting to decommission this core.
    /// </summary>
    /// <remarks>
    /// This method calls Release() to drive the refcount toward zero. Note that the
    /// factory map does NOT hold its own refcount increment — the "first handle"
    /// refcount baked into the constructor's _refCount=1 is owned by the first
    /// returned handle, not by the map. So calling Release() here borrows against
    /// whichever handle still holds that refcount slot:
    ///
    /// - If any handles are still live at reset time, Release() decrements the shared
    ///   refcount by 1. Those handles' own Dispose() calls will still decrement once
    ///   each; the advisory `current &lt;= 0` guard in Release() makes any resulting
    ///   over-release a safe no-op.
    /// - If no handles are live (the map entry exists but was never wrapped in an
    ///   additional handle beyond the first, which was already disposed), Release()
    ///   drives refcount to 0 and Shutdown() runs cleanly.
    ///
    /// The ResetForTesting caller also removes the map entry directly via
    /// ICollection.Remove(kvp), so Shutdown's own map-cleanup becomes a no-op. This
    /// sequence works correctly but relies on the advisory guard rather than on a
    /// dedicated map-held reference; do not "fix" it by adding a separate acquire
    /// for the map without also updating the factory's refcount model.
    /// </remarks>
    internal void ForceShutdownFromReset()
    {
        Release();
    }

    /// <summary>
    /// Atomically increments the refcount if the core is still alive.
    /// Returns false if the core has already shut down (caller must construct a new one).
    /// Safe against over-release: negative refcount values are treated as shut down.
    /// </summary>
    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Decrements the refcount. When it reaches zero, runs the real shutdown exactly once.
    /// Over-release (calling Release more times than TryAcquire was called) is a no-op —
    /// the advisory guard prevents the counter from drifting below zero for the common case,
    /// and TryAcquire's &lt;= 0 check is the backstop for any racing over-release.
    /// </summary>
    public void Release()
    {
        var current = Volatile.Read(ref _refCount);
        if (current <= 0) return;
        var newCount = Interlocked.Decrement(ref _refCount);
        if (newCount == 0)
        {
            if (Interlocked.Exchange(ref _isShutDown, 1) == 0)
            {
                Shutdown();
            }
        }
    }

    /// <summary>Evaluates a flag and returns full detail. Does not track the evaluation event.</summary>
    /// <remarks>
    /// This is the single evaluation choke point — every <c>BoolVariation</c>/
    /// <c>StringVariation</c>/<c>VariationDetail</c>/… call funnels through it. Registered
    /// evaluation inspectors are notified exactly once per call, on every exit path, with
    /// the reason and value the caller actually receives — except after the core has shut
    /// down, where the evaluation still returns its usual value but fires no inspectors.
    /// </remarks>
    public EvaluationDetail<T> Evaluate<T>(string key, EvaluationContext context, T defaultValue)
    {
        try
        {
            if (!_store.TryGetFlag(key, out var flag) || flag == null)
            {
                NotifyInspectors(
                    key,
                    context,
                    defaultValue,
                    EvaluationReason.FlagNotFound,
                    variationKey: null,
                    ruleId: null,
                    prerequisiteKey: null);

                return new EvaluationDetail<T>(
                    defaultValue,
                    EvaluationReason.FlagNotFound,
                    null,
                    $"Flag '{key}' not found");
            }

            var result = _evaluator.Evaluate(
                flag,
                context,
                _store.GetAllFlags(),
                segKey =>
                {
                    _store.TryGetSegment(segKey, out var seg);
                    return seg;
                });
            var variation = flag.Variations.FirstOrDefault(v => v.Key == result.VariationKey);

            if (variation == null)
            {
                // Malformed config: the evaluator picked a variation key the flag doesn't
                // define. Reported as the contract's Error path — variationKey/ruleId/
                // prerequisiteKey stay null there, even though the returned detail still
                // carries them for diagnostics.
                NotifyInspectors(
                    key,
                    context,
                    defaultValue,
                    EvaluationReason.Error,
                    variationKey: null,
                    ruleId: null,
                    prerequisiteKey: null);

                return new EvaluationDetail<T>(
                    defaultValue,
                    EvaluationReason.Error,
                    result.RuleId,
                    $"Variation '{result.VariationKey}' not found in flag '{key}'",
                    result.VariationKey,
                    result.PrerequisiteKey);
            }

            var value = DeserializeValue<T>(variation.Value);

            NotifyInspectors(
                key,
                context,
                value,
                result.Reason,
                result.VariationKey,
                result.RuleId,
                result.PrerequisiteKey);

            return new EvaluationDetail<T>(value, result.Reason, result.RuleId, null, result.VariationKey, result.PrerequisiteKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating flag {FlagKey}", key);

            NotifyInspectors(
                key,
                context,
                defaultValue,
                EvaluationReason.Error,
                variationKey: null,
                ruleId: null,
                prerequisiteKey: null);

            return new EvaluationDetail<T>(
                defaultValue,
                EvaluationReason.Error,
                null,
                ex.Message);
        }
    }

    /// <summary>
    /// Drops null entries from the configured inspector collection and freezes the
    /// remainder into an array. Callers bypass nullable reference annotations at
    /// runtime, so a null entry is dropped here rather than throwing on the hot path.
    /// </summary>
    private static EvaluationInspector[] NormalizeInspectors(IList<EvaluationInspector>? configured)
    {
        if (configured == null || configured.Count == 0)
        {
            return Array.Empty<EvaluationInspector>();
        }

        List<EvaluationInspector>? kept = null;
        foreach (var inspector in configured)
        {
            if (inspector is null) continue;
            kept ??= new List<EvaluationInspector>(configured.Count);
            kept.Add(inspector);
        }

        return kept == null ? Array.Empty<EvaluationInspector>() : kept.ToArray();
    }

    /// <summary>
    /// Fires the registered evaluation inspectors. Called once per variation call on
    /// every exit path of <see cref="Evaluate{T}"/>. Never throws: a throwing inspector
    /// is isolated so it neither changes the value the caller receives nor stops the
    /// remaining inspectors. Notification is suppressed once the core has shut down.
    /// </summary>
    private void NotifyInspectors<T>(
        string flagKey,
        EvaluationContext context,
        T value,
        EvaluationReason reason,
        string? variationKey,
        string? ruleId,
        string? prerequisiteKey)
    {
        // Hot path: with no inspectors registered, allocate nothing and return.
        var inspectors = _inspectors;
        if (inspectors.Length == 0) return;

        // A closed client emits no inspector events (cross-SDK contract). The core is
        // shut down once its last handle is disposed, which is the same lifecycle flag
        // TrackEvaluation/Flush already gate on — no new state. This suppresses the
        // notification ONLY: a post-dispose evaluation still returns exactly the value
        // it would have returned before, because the guard is here and not in Evaluate.
        if (IsShutDown) return;

        EvaluationEvent evaluationEvent;
        try
        {
            evaluationEvent = new EvaluationEvent(
                flagKey,
                context?.Copy() ?? new EvaluationContext(),
                value,
                variationKey,
                reason,
                ruleId,
                prerequisiteKey,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // Building the event must never disturb evaluation (e.g. a caller mutating
            // its own context concurrently). Skip this notification rather than letting
            // the throw reach Evaluate's catch and double-fire the inspectors.
            _logger.LogWarning(ex, "Failed to build evaluation inspector event for flag {FlagKey}", flagKey);
            return;
        }

        foreach (var inspector in inspectors)
        {
            try
            {
                inspector(evaluationEvent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Evaluation inspector threw for flag {FlagKey}", flagKey);
            }
        }
    }

    /// <summary>
    /// Registers a listener for flag-configuration changes. Dispose the returned handle to
    /// unsubscribe. Listeners live on the shared core, so every handle for this SDK key
    /// observes every update regardless of which handle registered.
    /// </summary>
    public IDisposable OnFlagsChanged(Action<IReadOnlyCollection<string>> listener)
    {
        if (listener == null) throw new ArgumentNullException(nameof(listener));

        lock (_flagsChangedLock)
        {
            var current = _flagsChangedListeners;
            var next = new Action<IReadOnlyCollection<string>>[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = listener;
            _flagsChangedListeners = next;
        }

        return new FlagsChangedSubscription(this, listener);
    }

    private void RemoveFlagsChangedListener(Action<IReadOnlyCollection<string>> listener)
    {
        lock (_flagsChangedLock)
        {
            var current = _flagsChangedListeners;
            var index = Array.IndexOf(current, listener);
            if (index < 0) return;

            if (current.Length == 1)
            {
                _flagsChangedListeners = Array.Empty<Action<IReadOnlyCollection<string>>>();
                return;
            }

            var next = new Action<IReadOnlyCollection<string>>[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            _flagsChangedListeners = next;
        }
    }

    /// <summary>
    /// Fans a store change out to the registered listeners. The store's own notifier
    /// swallows exceptions silently and has no logger, so isolation happens here: a
    /// throwing listener neither skips its siblings nor escapes into the polling/SSE loop,
    /// and is logged at warning. Suppressed once the core has shut down.
    /// </summary>
    private void NotifyFlagsChanged(IReadOnlyCollection<string> keys)
    {
        var listeners = _flagsChangedListeners;
        if (listeners.Length == 0) return;
        if (IsShutDown) return;

        foreach (var listener in listeners)
        {
            try
            {
                listener(keys);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flag-change listener threw for {ChangedFlagCount} changed flags", keys.Count);
            }
        }
    }

    private sealed class FlagsChangedSubscription : IDisposable
    {
        private SharedFeatureflipCore? _core;
        private readonly Action<IReadOnlyCollection<string>> _listener;

        public FlagsChangedSubscription(SharedFeatureflipCore core, Action<IReadOnlyCollection<string>> listener)
        {
            _core = core;
            _listener = listener;
        }

        public void Dispose()
        {
            var core = Interlocked.Exchange(ref _core, null);
            core?.RemoveFlagsChangedListener(_listener);
        }
    }

    /// <summary>
    /// Queues an evaluation event for the analytics batch.
    /// </summary>
    /// <remarks>
    /// The context is optional. A null one is a legitimate "no user" evaluation —
    /// <see cref="Evaluate{T}"/> serves the fallthrough for it and NotifyInspectors
    /// substitutes an empty context — so it must not be able to fail a flag read here.
    /// This runs <em>after</em> Evaluate has computed the caller's answer and outside
    /// the try/catch that turns evaluation failures into the caller's default, so
    /// dereferencing a null context threw a bare NullReferenceException out of a call
    /// that had already succeeded (#2311). Callers bypass nullable reference
    /// annotations at runtime — the same reason NormalizeInspectors drops null
    /// entries rather than throwing. The event is still recorded, just unattributed:
    /// SdkEvent.UserId is nullable, as is the eval-api's SdkEventDto.UserId. That
    /// matches the Java SDK (#2280) and the Go SDK, whose value-typed context yields
    /// an empty user id for the same case.
    /// </remarks>
    public void TrackEvaluation<T>(string key, EvaluationContext context, EvaluationDetail<T> detail)
    {
        if (IsShutDown) return;

        var evt = new SdkEvent
        {
            Type = "Evaluation",
            FlagKey = key,
            UserId = context?.UserId,
            Variation = detail.VariationKey,
            Timestamp = DateTime.UtcNow
        };

        _eventProcessor.Enqueue(evt);

        // Flush on batch size, unless a retryable failure has put the size trigger in
        // backoff or an earlier size-triggered flush is still in flight.
        if (_eventProcessor.ShouldFlush()
            && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _nextAutoFlushTicks)
            && Interlocked.CompareExchange(ref _autoFlushInFlight, 1, 0) == 0)
        {
            _ = AutoFlushAsync();
        }
    }

    /// <summary>
    /// Runs a batch-size-triggered flush and releases the in-flight latch afterwards.
    /// </summary>
    private async Task AutoFlushAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // FlushAsync already classified and logged this; there is no caller to observe
            // a throw on the fire-and-forget path, and letting it escape would surface as
            // an unobserved task exception.
        }
        finally
        {
            Interlocked.Exchange(ref _autoFlushInFlight, 0);
        }
    }

    public void Flush()
    {
        if (IsShutDown) return;

        try
        {
            // Use async version but with a reasonable timeout to avoid indefinite blocking
            FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex.InnerException ?? ex, "Failed to flush events");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush events");
        }
    }

    /// <summary>
    /// Drains the event queue, coalescing with any drain already in progress.
    /// </summary>
    /// <remarks>
    /// At most one drain runs at a time. A caller that arrives while one is already
    /// going awaits it and returns — it does NOT start its own, and it does NOT return
    /// early, because a caller that awaited <c>FlushAsync</c> is asking for its events
    /// to be sent. This matches the js/node SDKs, whose <c>flush()</c> has always
    /// returned the in-flight promise. The coalesced caller's own
    /// <paramref name="cancellationToken"/> still aborts its wait, without cancelling
    /// the shared drain out from under the caller that started it.
    /// </remarks>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (IsShutDown) return Task.CompletedTask;

        if (_httpClient == null) return Task.CompletedTask;

        lock (_flushGate)
        {
            if (_inFlightFlush is { } existing) return AwaitInFlightFlushAsync(existing, cancellationToken);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightFlush = completion.Task;
            return RunDrainAsync(completion, cancellationToken);
        }
    }

    /// <summary>Runs the drain and releases the coalescing handle afterwards.</summary>
    private async Task RunDrainAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        try
        {
            await DrainEventsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_flushGate) _inFlightFlush = null;
            // Always a result, never an exception: waiters are signalled that the drain
            // is over, not handed a failure they did not cause and cannot act on.
            completion.TrySetResult(true);
        }
    }

    /// <summary>Waits for an in-flight drain, honouring the waiter's own token.</summary>
    private static async Task AwaitInFlightFlushAsync(Task inFlight, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await inFlight.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            if (await Task.WhenAny(inFlight, cancelled.Task).ConfigureAwait(false) != inFlight)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>The drain loop itself, callable when coalescing must be bypassed.</summary>
    private async Task DrainEventsAsync(CancellationToken cancellationToken)
    {
        // Captured once: FlushAsync established this is non-null, but the field is
        // nullable and this method no longer sits behind that check.
        var httpClient = _httpClient;
        if (httpClient == null) return;

        // One request per batch rather than one for the whole queue. Re-queuing failures
        // (#2456) is what lets the queue grow to its bound during an outage, and posting
        // 10,000 events at once risks a body the server rejects outright — a 413 is
        // non-retryable, so the backlog would be dropped by the very path meant to keep it.
        while (true)
        {
            // Drained BEFORE the send, so the batch has to be held here to be put back if
            // the send fails. Without that a single transient failure discarded it, and the
            // production edge answers this endpoint with a 503 at a low constant rate — so
            // evaluation analytics were being lost steadily.
            var events = _eventProcessor.DrainBatch(_options.FlushBatchSize);
            if (events.Count == 0) return;

            try
            {
                await httpClient.SendEventsAsync(events, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _nextAutoFlushTicks, 0);
                _logger.LogDebug("Flushed {EventCount} events", events.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled the flush; the server never rejected this batch. Keep
                // it so a later flush can still deliver it, then let cancellation propagate.
                _eventProcessor.Requeue(events);
                throw;
            }
            catch (Exception ex) when (IsRetryableFlushFailure(ex))
            {
                var dropped = _eventProcessor.Requeue(events);
                Interlocked.Exchange(ref _nextAutoFlushTicks, DateTime.UtcNow.Add(_options.FlushInterval).Ticks);
                _logger.LogWarning(ex,
                    "Failed to flush {EventCount} events; re-queued for the next flush ({DroppedCount} dropped to stay within the queue bound)",
                    events.Count, dropped);
                // Stop here. The batch is back at the head of the queue this loop is
                // draining, so continuing would re-send it immediately and spin for as long
                // as the endpoint stays down.
                return;
            }
            catch (Exception ex)
            {
                // Dropped, so the queue shrinks and the loop still terminates — move on to
                // the next batch rather than letting one poison batch block the backlog.
                _logger.LogWarning(ex,
                    "Failed to flush {EventCount} events; dropped because the failure is not retryable",
                    events.Count);
            }
        }
    }

    /// <summary>
    /// Whether a failed flush could plausibly succeed if the same batch were sent again.
    /// </summary>
    /// <remarks>
    /// Transport faults (connection reset, DNS, TLS) and request timeouts are transient by
    /// nature. For an HTTP answer the status decides: 5xx and 429 are retryable; anything
    /// else — 401/403 means the SDK key was rejected, 400 means the body is malformed —
    /// will fail identically next time, and retrying it forever would pin the queue at its
    /// bound and starve every later event.
    /// </remarks>
    private static bool IsRetryableFlushFailure(Exception ex) => ex switch
    {
        EventSendException send => send.IsRetryable,
        // Reaching here means no cancellation was requested by the caller (that case is
        // handled by the catch above), so this is the HttpClient.Timeout elapsing.
        OperationCanceledException => true,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Converts a served variation value to the type the caller asked for, strictly.
    /// </summary>
    /// <remarks>
    /// The <see cref="JsonElement"/> accessors already throw on a type mismatch, which is
    /// what makes the caller's default the returned value. This method deliberately does
    /// NOT swallow that throw: letting it reach <see cref="Evaluate{T}"/>'s handler is
    /// what turns a mismatched read into <see cref="EvaluationReason.Error"/> instead of
    /// the evaluator's success reason, so callers can actually detect it (#2281).
    /// Swallowing it here returned the right value with a reason that said everything
    /// was fine.
    /// </remarks>
    private static T DeserializeValue<T>(JsonElement element)
    {
        if (typeof(T) == typeof(bool))
            return (T)(object)element.GetBoolean();
        if (typeof(T) == typeof(string))
        {
            var strValue = element.GetString()
                ?? throw new InvalidOperationException("Value is JSON null, not a string.");
            return (T)(object)strValue;
        }
        if (typeof(T) == typeof(int))
            return (T)(object)element.GetInt32();
        if (typeof(T) == typeof(double))
            return (T)(object)element.GetDouble();
#if NETSTANDARD2_0
        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException($"Value is not convertible to {typeof(T).Name}.");
#else
        return element.Deserialize<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Value is not convertible to {typeof(T).Name}.");
#endif
    }

    private void InitializeSync()
    {
        var loadTask = LoadFlagsAsync(_cts.Token);
        if (!loadTask.Wait(_options.InitTimeout))
        {
            if (_options.StartOffline)
            {
                _logger.LogWarning("Initialization timed out after {Timeout}s, starting in offline mode",
                    _options.InitTimeout.TotalSeconds);
                _initializationTcs.TrySetResult(false);
                return;
            }
            throw new FeatureFlagInitializationException(
                $"Initialization timed out after {_options.InitTimeout.TotalSeconds} seconds.");
        }

        if (loadTask.IsFaulted && loadTask.Exception != null)
        {
            var innerException = loadTask.Exception.InnerException ?? loadTask.Exception;
            if (_options.StartOffline)
            {
                _logger.LogWarning(innerException, "Initialization failed, starting in offline mode");
                _initializationTcs.TrySetResult(false);
                return;
            }
            throw new FeatureFlagInitializationException(
                "Failed to initialize feature flag client.", innerException);
        }

        _isInitialized = true;
        _initializationTcs.TrySetResult(true);
        _logger.LogDebug("Feature flag client initialized successfully");
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await LoadFlagsAsync(ct).ConfigureAwait(false);
            _isInitialized = true;
            _initializationTcs.TrySetResult(true);
            _logger.LogDebug("Feature flag client initialized successfully (async)");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Background initialization failed, will retry during refresh");
            // Don't set TCS here - let SSE/polling set it when they succeed
        }
    }

    private async Task LoadFlagsAsync(CancellationToken ct)
    {
        if (_httpClient == null) return;

        var response = await _httpClient.GetFlagsAsync(ct).ConfigureAwait(false);
        if (response != null)
        {
            // Use Replace for full updates to handle deleted flags
            _store.Replace(response.Flags, response.Segments);
            _logger.LogDebug("Loaded {FlagCount} flags and {SegmentCount} segments",
                response.Flags.Count, response.Segments.Count);
        }
    }

    /// <summary>
    /// Capped exponential reconnect backoff. Every delay carries [d/2, d] jitter —
    /// including the first (failures &lt;= 1) — then doubles up to
    /// <paramref name="maxDelay"/>. Never terminal: there is no maximum retry count.
    /// <para>
    /// The jitter is load-bearing on the FIRST reconnect, not just the escalating
    /// ones: the drops this absorbs are fleet-wide (a single edge event severs every
    /// stream at once — #2457), so every client re-enters here at failures == 0
    /// together. Returning a constant there replayed the drop's own synchronisation
    /// as a reconnect spike one base delay later (#2508). Jitter keeps the delay
    /// strictly positive, so a clean EOF still cannot busy-loop.
    /// </para>
    /// </summary>
    internal static TimeSpan ComputeReconnectDelay(int consecutiveFailures, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (consecutiveFailures <= 1) return WithJitter(baseDelay);
        var delay = baseDelay;
        for (var i = 1; i < consecutiveFailures; i++)
        {
            delay += delay; // double
            if (delay >= maxDelay) return WithJitter(maxDelay);
        }
        return WithJitter(delay);
    }

    // Returns a value in [d/2, d] to de-correlate reconnects across many SDK
    // instances (thundering-herd avoidance after a shared outage).
    private static TimeSpan WithJitter(TimeSpan d)
    {
        if (d <= TimeSpan.Zero) return d;
        var half = d.Ticks / 2;
        return TimeSpan.FromTicks(half + (long)(NextJitter() * half));
    }

#if NETSTANDARD2_0
    private static readonly Random _jitterRng = new Random();
    private static double NextJitter()
    {
        // Random is not thread-safe; the reconnect loop is single-threaded per core,
        // but lock defensively — jitter quality is non-critical.
        lock (_jitterRng) return _jitterRng.NextDouble();
    }
#else
    private static double NextJitter() => Random.Shared.NextDouble();
#endif

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        // Wait for initialization to complete first to avoid race conditions
        // This ensures SSE patches aren't applied to an empty store that then gets
        // overwritten by a late initialization
        try
        {
            var timeout = _options.InitTimeout + TimeSpan.FromSeconds(5);
            var completedTask = await Task.WhenAny(
                _initializationTcs.Task,
                Task.Delay(timeout, ct)).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Initialization timed out or failed, proceed anyway - SSE/polling will initialize
        }

        var sseFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_options.Streaming)
                {
                    var reached = await RefreshViaSseAsync(ct).ConfigureAwait(false);
                    sseFailures = reached ? 0 : sseFailures + 1;

                    // Always back off before reconnecting the stream. A clean/immediate
                    // EOF returns without throwing, so without this delay the loop would
                    // busy-reconnect (GAP 6). A healthy stream resets to the base delay;
                    // repeated failures escalate up to the cap. Never terminal.
                    var reconnectDelay = ComputeReconnectDelay(sseFailures, SseReconnectBaseDelay, SseReconnectMaxDelay);
                    await Task.Delay(reconnectDelay, ct).ConfigureAwait(false);
                }
                else
                {
                    await RefreshViaPollingAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                sseFailures++;
                var reconnectDelay = ComputeReconnectDelay(sseFailures, SseReconnectBaseDelay, SseReconnectMaxDelay);
                _logger.LogWarning(ex, "Error in refresh loop, will retry in {Interval}s", reconnectDelay.TotalSeconds);
                await Task.Delay(reconnectDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> RefreshViaSseAsync(CancellationToken ct)
    {
        if (_httpClient == null) return false;

        _logger.LogDebug("Connecting to SSE stream");
        var reached = false;

        // Liveness watchdog (see SseIdleTimeout): cancel the read if no bytes
        // arrive for the idle window, so a half-open socket reconnects instead of
        // blocking forever. Reset on every event below.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(SseIdleTimeout);

        try
        {
            using var stream = await _httpClient.GetStreamAsync(idleCts.Token).ConfigureAwait(false);
            using var reader = new SseStreamReader(stream, _logger);

            _logger.LogInformation("Connected to SSE stream for real-time updates");

            await foreach (var evt in reader.ReadEventsAsync(idleCts.Token).ConfigureAwait(false))
            {
                idleCts.CancelAfter(SseIdleTimeout); // liveness: a ping/event proves the socket is alive
                // Only a `sync` frame (the full connect snapshot) counts as a
                // healthy reach that resets the failure counter — a lone ping (or
                // stray delta) then EOF must not mask a persistently-broken stream
                // and keep the polling fallback from arming.
                if (evt.Type == SseEventType.Sync) reached = true;
                await ProcessSseEventAsync(evt).ConfigureAwait(false);
            }

            // The stream opened but ended without ever delivering a `sync`, so it carried
            // no configuration — a genuinely degraded stream, not the routine mid-frame
            // sever behind a CDN. SseStreamReader logs that sever at Debug because it
            // cannot tell the two apart (#2457); this is the layer that can, so the
            // degraded case keeps its Warning. Excluded on shutdown, where the reader
            // stops by design rather than by failure.
            if (!reached && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("SSE stream ended without delivering a snapshot; reconnecting");
            }
        }
        catch (HttpRequestException ex)
        {
            // Connection/auth failure (e.g. 401/403/5xx during an outage). NOT
            // terminal — do a single corrective poll to keep last-known-good
            // fresh, then let the loop's reconnect backoff handle the wait (do
            // NOT also wait a full PollInterval here — that double-delays reconnect).
            _logger.LogWarning(ex, "SSE connection failed, polling once then retrying stream");
            await PollOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Idle watchdog fired while connecting (no bytes within the window) —
            // half-open socket. Return so the loop reconnects/backs off; a
            // shutdown cancellation (ct) is rethrown for the loop to break on.
            _logger.LogWarning("SSE stream idle for {Timeout}s; reconnecting", SseIdleTimeout.TotalSeconds);
        }

        return reached;
    }

    internal async Task ProcessSseEventAsync(SseEvent evt)
    {
        switch (evt.Type)
        {
            case SseEventType.FlagCreated:
            case SseEventType.FlagUpdated:
                if (!string.IsNullOrEmpty(evt.FlagKey))
                {
                    try
                    {
                        var flag = await _httpClient!.GetFlagAsync(evt.FlagKey!).ConfigureAwait(false);
                        if (flag != null)
                        {
                            _store.Upsert(new List<FlagConfiguration> { flag });
                            MarkInitializedIfNeeded();
                            _logger.LogDebug("SSE {EventType}: upserted flag {FlagKey}", evt.Type, evt.FlagKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch flag {FlagKey} after SSE event", evt.FlagKey);
                    }
                }
                break;

            case SseEventType.FlagDeleted:
                if (!string.IsNullOrEmpty(evt.FlagKey))
                {
                    _store.RemoveFlag(evt.FlagKey!);
                    _logger.LogDebug("SSE flag.deleted: removed flag {FlagKey}", evt.FlagKey);
                }
                break;

            case SseEventType.SegmentUpdated:
                try
                {
                    var response = await _httpClient!.GetFlagsAsync().ConfigureAwait(false);
                    if (response != null)
                    {
                        _store.Replace(response.Flags, response.Segments);
                        MarkInitializedIfNeeded();
                        _logger.LogDebug("SSE segment.updated: refetched all flags");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refetch flags after segment.updated SSE event");
                }
                break;

            case SseEventType.Sync:
                // Full config snapshot on (re)connect. Replace the whole store so
                // flags changed — or deleted — during a disconnect are re-synced.
                // Full replace, never merge (mirrors polling + segment.updated).
                if (evt.Snapshot != null)
                {
                    _store.Replace(evt.Snapshot.Flags, evt.Snapshot.Segments);
                    MarkInitializedIfNeeded();
                    _logger.LogDebug("SSE sync: replaced store with {FlagCount} flags", evt.Snapshot.Flags?.Count ?? 0);
                }
                break;

            case SseEventType.Ping:
                break;
        }
    }

    private void MarkInitializedIfNeeded()
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            _initializationTcs.TrySetResult(true);
            _logger.LogInformation("Feature flag client initialized via SSE");
        }
    }

    private async Task RefreshViaPollingAsync(CancellationToken ct)
    {
        await PollOnceAsync(ct).ConfigureAwait(false);
        await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
    }

    // A single corrective poll with no trailing interval wait — used by the
    // SSE-failure path, where the reconnect loop already applies its own backoff.
    // RefreshViaPollingAsync (the non-streaming poll loop) adds the interval delay.
    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await LoadFlagsAsync(ct).ConfigureAwait(false);
            if (!_isInitialized)
            {
                _isInitialized = true;
                _initializationTcs.TrySetResult(true);
                _logger.LogInformation("Feature flag client initialized via polling");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to refresh flags via polling");
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.FlushInterval, ct).ConfigureAwait(false);
                await FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in flush loop");
            }
        }
    }

    private void Shutdown()
    {
        _storeSubscription.Dispose();

        if (_owningMap != null && _owningKey != null)
        {
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, SharedFeatureflipCore>>)_owningMap)
                .Remove(new System.Collections.Generic.KeyValuePair<string, SharedFeatureflipCore>(_owningKey, this));
        }

        try
        {
            _cts.Cancel();

            try
            {
                var events = _eventProcessor.Drain();
                if (events.Count > 0 && _httpClient != null)
                {
                    _httpClient.SendEventsAsync(events).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // Ignore errors during disposal flush
            }

            var tasks = new List<Task>();
            if (_flushTask != null) tasks.Add(_flushTask);
            if (_refreshTask != null) tasks.Add(_refreshTask);

            if (tasks.Count > 0)
            {
                try
                {
                    Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Ignore cancellation exceptions
                }
            }
        }
        finally
        {
            _eventProcessor.Dispose();
            _httpClient?.Dispose();
            _cts.Dispose();
        }
    }

    public Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
    {
        var initTask = _initializationTcs.Task;
        if (initTask.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return initTask;
        }

        return WaitWithCancellationAsync(initTask, cancellationToken);
    }

    private static async Task WaitWithCancellationAsync(Task<bool> initTask, CancellationToken cancellationToken)
    {
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelTcs))
        {
            var completed = await Task.WhenAny(initTask, cancelTcs.Task).ConfigureAwait(false);
            if (completed != initTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        await initTask.ConfigureAwait(false);
    }

    public void Dispose() => Release();
}
