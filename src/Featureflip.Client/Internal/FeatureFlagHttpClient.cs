using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

internal sealed class FeatureFlagHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _streamingClient;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Optional so the many tests that construct this client directly need not supply one.
    /// The core passes its own, which is what makes an entity dropped for an unevaluable
    /// enum (#2402) visible rather than silent.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// The SHIPPED deserializer options, exposed so tests can assert against the real
    /// configuration rather than a replica — a test-local copy would keep passing if this
    /// configuration regressed, which is precisely how a converter registration silently
    /// goes missing.
    /// </summary>
    internal JsonSerializerOptions JsonOptions => _jsonOptions;
    private bool _disposed;

    public FeatureFlagHttpClient(string sdkKey, FeatureFlagOptions options, ILogger? logger = null)
    {
        _logger = logger;
        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        // One-shot GET/POST client. HttpClient.Timeout is a *total* request timeout, which
        // is the correct bound for the short, bounded flag/event requests.
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = options.ReadTimeout
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", sdkKey);
        SdkIdentity.Apply(_httpClient.DefaultRequestHeaders, options, logger);

        // Dedicated client for the long-lived SSE stream. HttpClient.Timeout is a *total*
        // request timeout in .NET, so applying the finite ReadTimeout here would abort the
        // stream — even bounding its time-to-first-byte — and real-time flag updates would
        // never sustain (#1526). The stream runs unbounded and relies on the caller's
        // CancellationToken for shutdown (and ConnectTimeout, where supported, to bound
        // connection establishment). Mirrors the Go/Java/Python SDKs.
        _streamingClient = CreateStreamingClient(baseAddress, options.ConnectTimeout);
        _streamingClient.DefaultRequestHeaders.Add("Authorization", sdkKey);
        // The stream's own client needs the token too; it shares no headers with the one above.
        // Applied with a null logger so an invalid wrapper warns once, not once per client.
        SdkIdentity.Apply(_streamingClient.DefaultRequestHeaders, options, logger: null);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // TolerantConditionOperatorConverter FIRST: System.Text.Json picks the first
            // converter that handles a type, so it must precede the stock enum converter to
            // win for ConditionOperator. It maps an unknown operator to a sentinel instead of
            // throwing, which previously failed the WHOLE flags payload over one condition (#2372).
            //
            // allowIntegerValues: false rejects an integer enum, a TYPE violation — see
            // SseStreamReader.SnapshotJsonOptions for why that default is a silent-wrong
            // hazard, and why it is a different axis from #2372's string tolerance (#2315).
            Converters =
            {
                new TolerantConditionOperatorConverter(),
                new TolerantFlagTypeConverter(),
                new TolerantServeTypeConverter(),
                new TolerantConditionLogicConverter(),
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)
            }
        };
    }

    private static HttpClient CreateStreamingClient(Uri baseAddress, TimeSpan connectTimeout)
    {
#if NETSTANDARD2_0
        // netstandard2.0 (e.g. .NET Framework) has no SocketsHttpHandler.ConnectTimeout, so
        // connection establishment falls back to the platform default. The infinite total
        // timeout is the essential fix: the stream is never aborted by the finite ReadTimeout.
        return new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
#else
        var handler = new SocketsHttpHandler
        {
            // Bound only connection establishment; the stream itself must never time out.
            // A non-positive ConnectTimeout is rejected by SocketsHttpHandler, so treat it
            // as unbounded rather than crashing construction.
            ConnectTimeout = connectTimeout > TimeSpan.Zero
                ? connectTimeout
                : System.Threading.Timeout.InfiniteTimeSpan
        };
        return new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
#endif
    }

    public async Task<GetFlagsResponse?> GetFlagsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("v1/sdk/flags", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var parsed = await response.Content
            .ReadFromJsonAsync<GetFlagsResponse>(_jsonOptions, ct).ConfigureAwait(false);
        // Applied here rather than at each store.Replace call site, so polling and the SSE
        // segment.updated refetch cannot drift; the sync frame parses its own payload and
        // drops there (#2402).
        return parsed == null ? null : UnevaluableEntities.DropUnevaluable(parsed, _logger);
    }

    public async Task<FlagConfiguration?> GetFlagAsync(string key, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v1/sdk/flags/{Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var flag = await response.Content
            .ReadFromJsonAsync<FlagConfiguration>(_jsonOptions, ct).ConfigureAwait(false);
        if (flag == null) return null;

        // An unevaluable enum drops the flag rather than upserting it (#2402). For a delta
        // whose whole scope is one flag that means leaving the store's previous copy alone:
        // replacing it with one this build would mis-evaluate is the outcome the drop
        // exists to prevent, and the caller's existing null check already skips the upsert.
        var reason = UnevaluableEntities.FlagReason(flag);
        if (reason != null)
        {
            _logger?.LogWarning(
                "Dropping flag delta for {FlagKey}: {Reason}. This SDK version may be older "
                + "than the flag configuration.", key, reason);
            return null;
        }

        return flag;
    }

    public async Task SendEventsAsync(IReadOnlyList<SdkEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        var request = new SendEventsRequest { Events = events };
        var response = await _httpClient.PostAsJsonAsync("v1/sdk/events", request, _jsonOptions, ct).ConfigureAwait(false);
        // Not EnsureSuccessStatusCode: the flush path has to know WHICH status came back so
        // it can keep the batch on a retryable failure instead of discarding it (#2456), and
        // HttpRequestException.StatusCode is unavailable on netstandard2.0.
        if (!response.IsSuccessStatusCode)
            throw new EventSendException((int)response.StatusCode);
    }

    public async Task<Stream> GetStreamAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "v1/sdk/stream");
        var response = await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
        // Note: netstandard2.0 ReadAsStreamAsync doesn't accept CancellationToken.
        // Cancellation is still respected by SendAsync above.
        return await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _streamingClient.Dispose();
    }

    private sealed class SendEventsRequest
    {
        public IReadOnlyList<SdkEvent> Events { get; set; } = Array.Empty<SdkEvent>();
    }
}
