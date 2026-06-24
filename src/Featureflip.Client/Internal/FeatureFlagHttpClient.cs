using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

internal sealed class FeatureFlagHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _streamingClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public FeatureFlagHttpClient(string sdkKey, FeatureFlagOptions options)
    {
        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        // One-shot GET/POST client. HttpClient.Timeout is a *total* request timeout, which
        // is the correct bound for the short, bounded flag/event requests.
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = options.ReadTimeout
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", sdkKey);

        // Dedicated client for the long-lived SSE stream. HttpClient.Timeout is a *total*
        // request timeout in .NET, so applying the finite ReadTimeout here would abort the
        // stream — even bounding its time-to-first-byte — and real-time flag updates would
        // never sustain (#1526). The stream runs unbounded and relies on the caller's
        // CancellationToken for shutdown (and ConnectTimeout, where supported, to bound
        // connection establishment). Mirrors the Go/Java/Python SDKs.
        _streamingClient = CreateStreamingClient(baseAddress, options.ConnectTimeout);
        _streamingClient.DefaultRequestHeaders.Add("Authorization", sdkKey);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(namingPolicy: null) }
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
        return await response.Content.ReadFromJsonAsync<GetFlagsResponse>(_jsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<FlagConfiguration?> GetFlagAsync(string key, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v1/sdk/flags/{Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FlagConfiguration>(_jsonOptions, ct).ConfigureAwait(false);
    }

    public async Task SendEventsAsync(IReadOnlyList<SdkEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        var request = new SendEventsRequest { Events = events };
        var response = await _httpClient.PostAsJsonAsync("v1/sdk/events", request, _jsonOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
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
