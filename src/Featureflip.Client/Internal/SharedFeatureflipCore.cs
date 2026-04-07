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

    private readonly FlagStore _store;
    private readonly FlagEvaluator _evaluator;
    private readonly EventProcessor _eventProcessor;
    private readonly FeatureFlagOptions _options;
    private readonly ILogger _logger;
    private readonly FeatureFlagHttpClient? _httpClient;
    private readonly CancellationTokenSource _cts;
    private readonly Task? _flushTask;
    private readonly Task? _refreshTask;
    private readonly TaskCompletionSource<bool> _initializationTcs;

    private int _refCount;
    private int _isShutDown;
    private volatile bool _isInitialized;

    private System.Collections.Concurrent.ConcurrentDictionary<string, SharedFeatureflipCore>? _owningMap;
    private string? _owningKey;

    public int RefCount => Volatile.Read(ref _refCount);
    public bool IsShutDown => Volatile.Read(ref _isShutDown) != 0;
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
        _store = new FlagStore();
        _evaluator = new FlagEvaluator();
        _eventProcessor = new EventProcessor(_options.FlushInterval, _options.FlushBatchSize);
        _httpClient = new FeatureFlagHttpClient(sdkKey, _options);
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
    private SharedFeatureflipCore(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _eventProcessor = new EventProcessor(options.FlushInterval, options.FlushBatchSize);
        _logger = NullLogger.Instance;
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
        return new SharedFeatureflipCore(store, new FlagEvaluator(), new FeatureFlagOptions());
    }

    internal static SharedFeatureflipCore CreateForTesting(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options)
    {
        return new SharedFeatureflipCore(store, evaluator, options);
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
    public EvaluationDetail<T> Evaluate<T>(string key, EvaluationContext context, T defaultValue)
    {
        try
        {
            if (!_store.TryGetFlag(key, out var flag) || flag == null)
            {
                return new EvaluationDetail<T>(
                    defaultValue,
                    EvaluationReason.FlagNotFound,
                    null,
                    $"Flag '{key}' not found");
            }

            var result = _evaluator.Evaluate(flag, context, segKey =>
            {
                _store.TryGetSegment(segKey, out var seg);
                return seg;
            });
            var variation = flag.Variations.FirstOrDefault(v => v.Key == result.VariationKey);

            if (variation == null)
            {
                return new EvaluationDetail<T>(
                    defaultValue,
                    EvaluationReason.Error,
                    result.RuleId,
                    $"Variation '{result.VariationKey}' not found in flag '{key}'",
                    result.VariationKey);
            }

            var value = DeserializeValue(variation.Value, defaultValue);
            return new EvaluationDetail<T>(value, result.Reason, result.RuleId, null, result.VariationKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating flag {FlagKey}", key);
            return new EvaluationDetail<T>(
                defaultValue,
                EvaluationReason.Error,
                null,
                ex.Message);
        }
    }

    public void TrackEvaluation<T>(string key, EvaluationContext context, EvaluationDetail<T> detail)
    {
        if (IsShutDown) return;

        var evt = new SdkEvent
        {
            Type = "Evaluation",
            FlagKey = key,
            UserId = context.UserId,
            Variation = detail.VariationKey,
            Timestamp = DateTime.UtcNow
        };

        _eventProcessor.Enqueue(evt);

        // Check if we should flush based on batch size.
        if (_eventProcessor.ShouldFlush())
        {
            _ = FlushAsync();
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

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (IsShutDown) return;

        try
        {
            var events = _eventProcessor.Drain();
            if (events.Count > 0 && _httpClient != null)
            {
                await _httpClient.SendEventsAsync(events, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Flushed {EventCount} events", events.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush events");
        }
    }

    private static T DeserializeValue<T>(JsonElement element, T defaultValue)
    {
        try
        {
            if (typeof(T) == typeof(bool))
                return (T)(object)element.GetBoolean();
            if (typeof(T) == typeof(string))
            {
                var strValue = element.GetString();
                return strValue != null ? (T)(object)strValue : defaultValue;
            }
            if (typeof(T) == typeof(int))
                return (T)(object)element.GetInt32();
            if (typeof(T) == typeof(double))
                return (T)(object)element.GetDouble();
#if NETSTANDARD2_0
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions) ?? defaultValue;
#else
            return element.Deserialize<T>(JsonOptions) ?? defaultValue;
#endif
        }
        catch
        {
            return defaultValue;
        }
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_options.Streaming)
                {
                    await RefreshViaSseAsync(ct).ConfigureAwait(false);
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
                _logger.LogWarning(ex, "Error in refresh loop, will retry in {Interval}s",
                    _options.PollInterval.TotalSeconds);
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshViaSseAsync(CancellationToken ct)
    {
        if (_httpClient == null) return;

        _logger.LogDebug("Connecting to SSE stream");

        try
        {
            using var stream = await _httpClient.GetStreamAsync(ct).ConfigureAwait(false);
            using var reader = new SseStreamReader(stream, _logger);

            _logger.LogInformation("Connected to SSE stream for real-time updates");

            await foreach (var evt in reader.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                await ProcessSseEventAsync(evt).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SSE connection failed, falling back to polling");
            // Fall back to polling on connection failure
            await RefreshViaPollingAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessSseEventAsync(SseEvent evt)
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

        await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
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

    public void Dispose() => Release();
}
