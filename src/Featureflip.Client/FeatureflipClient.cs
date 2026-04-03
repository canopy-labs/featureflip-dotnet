using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Featureflip.Client;

/// <summary>
/// The main client for evaluating feature flags.
/// </summary>
public sealed class FeatureflipClient : IFeatureflipClient
{
    private const string SdkKeyEnvVar = "FEATUREFLIP_SDK_KEY";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FlagStore _store;
    private readonly FlagEvaluator _evaluator;
    private readonly EventProcessor _eventProcessor;
    private readonly FeatureFlagOptions _options;
    private readonly FeatureFlagHttpClient? _httpClient;
    private readonly ILogger<FeatureflipClient> _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Task? _flushTask;
    private readonly Task? _refreshTask;
    private readonly TaskCompletionSource<bool> _initializationTcs;
    private volatile bool _isInitialized;
    private int _disposed; // 0 = not disposed, 1 = disposed; use Interlocked for thread safety

    /// <summary>
    /// Creates a new feature flag client.
    /// </summary>
    /// <param name="sdkKey">The SDK key for authentication. If null, reads from FEATUREFLIP_SDK_KEY environment variable.</param>
    /// <param name="options">Configuration options. If null, uses defaults.</param>
    /// <param name="logger">Optional logger instance.</param>
    /// <exception cref="FeatureFlagInitializationException">Thrown when initialization fails and WaitForInitialization is true.</exception>
    public FeatureflipClient(
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

        _options = options ?? new FeatureFlagOptions();
        _logger = logger ?? NullLogger<FeatureflipClient>.Instance;
        _store = new FlagStore();
        _evaluator = new FlagEvaluator();
        _eventProcessor = new EventProcessor(_options.FlushInterval, _options.FlushBatchSize);
        _httpClient = new FeatureFlagHttpClient(sdkKey, _options);
        _cts = new CancellationTokenSource();
        _initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (_options.WaitForInitialization)
            {
                // Block until flags are loaded or timeout expires
                InitializeSync();
            }
            else
            {
                // Start loading flags in background
                _logger.LogDebug("Starting background initialization");
                _ = InitializeAsync(_cts.Token);
            }

            // Start background tasks
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
    /// Internal constructor for testing. Does not start background tasks or HTTP client.
    /// </summary>
    internal FeatureflipClient(FlagStore store, FlagEvaluator evaluator, FeatureFlagOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _eventProcessor = new EventProcessor(options.FlushInterval, options.FlushBatchSize);
        _logger = NullLogger<FeatureflipClient>.Instance;
        _httpClient = null;
        _cts = new CancellationTokenSource();
        _initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _initializationTcs.TrySetResult(true);
        _flushTask = null;
        _refreshTask = null;
        _isInitialized = true;
    }

    /// <inheritdoc />
    public bool IsInitialized => _isInitialized;

    /// <inheritdoc />
    public T Variation<T>(string key, EvaluationContext context, T defaultValue)
    {
        var detail = EvaluateInternal<T>(key, context, defaultValue);
        TrackEvaluation(key, context, detail);
        return detail.Value;
    }

    /// <inheritdoc />
    public EvaluationDetail<T> VariationDetail<T>(string key, EvaluationContext context, T defaultValue)
    {
        var detail = EvaluateInternal<T>(key, context, defaultValue);
        TrackEvaluation(key, context, detail);
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

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

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
            // Cancellation requested, don't log as warning
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush events");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            // Signal cancellation
            _cts.Cancel();

            // Flush remaining events with a short timeout (fire-and-forget style)
            try
            {
                var events = _eventProcessor.Drain();
                if (events.Count > 0 && _httpClient != null)
                {
                    // Fire and forget with timeout - don't block disposal
                    _httpClient.SendEventsAsync(events).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // Ignore errors during disposal flush
            }

            // Wait for background tasks to complete
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

    private EvaluationDetail<T> EvaluateInternal<T>(string key, EvaluationContext context, T defaultValue)
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

            var result = _evaluator.Evaluate(flag, context, key =>
            {
                _store.TryGetSegment(key, out var seg);
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

    private void TrackEvaluation<T>(string key, EvaluationContext context, EvaluationDetail<T> detail)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var evt = new SdkEvent
        {
            Type = "Evaluation",
            FlagKey = key,
            UserId = context.UserId,
            Variation = detail.VariationKey,
            Timestamp = DateTime.UtcNow
        };

        _eventProcessor.Enqueue(evt);

        // Check if we should flush based on batch size
        if (_eventProcessor.ShouldFlush())
        {
            _ = FlushAsync();
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
}
