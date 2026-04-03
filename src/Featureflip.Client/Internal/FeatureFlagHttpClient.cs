using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

internal sealed class FeatureFlagHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public FeatureFlagHttpClient(string sdkKey, FeatureFlagOptions options)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = options.ReadTimeout
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", sdkKey);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(namingPolicy: null) }
        };
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
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
    }

    private sealed class SendEventsRequest
    {
        public IReadOnlyList<SdkEvent> Events { get; set; } = Array.Empty<SdkEvent>();
    }
}
