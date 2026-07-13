using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// Reads Server-Sent Events (SSE) from a stream and parses flag update messages.
/// </summary>
internal sealed class SseStreamReader : IDisposable
{
    // Deserializer for the `sync` snapshot payload — same shape/options as the
    // GET /v1/sdk/flags response (camelCase properties, string enums).
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly StreamReader _reader;
    private bool _disposed;

    public SseStreamReader(Stream stream, ILogger logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>
    /// Reads SSE messages from the stream and yields parsed events.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var eventType = string.Empty;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested && !_disposed)
        {
            string? line;
            try
            {
#if NETSTANDARD2_0
                line = await _reader.ReadLineAsync().ConfigureAwait(false);
#else
                line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
#endif
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "SSE stream read error");
                yield break;
            }

            if (line == null)
            {
                // End of stream
                yield break;
            }

            if (string.IsNullOrEmpty(line))
            {
                // Empty line = end of event
                if (dataBuilder.Length > 0)
                {
                    var data = dataBuilder.ToString();
                    dataBuilder.Clear();

                    var evt = ParseEvent(eventType, data);
                    if (evt != null)
                    {
                        yield return evt;
                    }
                }
                eventType = string.Empty;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }
                dataBuilder.Append(line.Substring(5).Trim());
            }
            else if (line.StartsWith(":", StringComparison.Ordinal))
            {
                // Comment/keepalive, ignore
            }
        }
    }

    private SseEvent? ParseEvent(string eventType, string data)
    {
        try
        {
            return eventType switch
            {
                "flag.created" => ParseFlagEvent(data, SseEventType.FlagCreated),
                "flag.updated" => ParseFlagEvent(data, SseEventType.FlagUpdated),
                "flag.deleted" => ParseFlagEvent(data, SseEventType.FlagDeleted),
                "segment.updated" => new SseEvent { Type = SseEventType.SegmentUpdated },
                "sync" => ParseSyncEvent(data),
                "ping" => new SseEvent { Type = SseEventType.Ping },
                _ => null
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse SSE event: {EventType}", eventType);
            return null;
        }
    }

    private static SseEvent ParseFlagEvent(string data, SseEventType type)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : (int?)null;
        return new SseEvent { Type = type, FlagKey = key, Version = version };
    }

    private static SseEvent ParseSyncEvent(string data)
    {
        // Full config snapshot the server sends on (re)connect. Carried whole so
        // the core can apply it as a store REPLACE (drop flags absent from it),
        // re-syncing anything changed/deleted during a disconnect.
        var snapshot = JsonSerializer.Deserialize<GetFlagsResponse>(data, SnapshotJsonOptions);
        return new SseEvent { Type = SseEventType.Sync, Snapshot = snapshot };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
        _stream.Dispose();
    }
}

internal enum SseEventType
{
    FlagCreated,
    FlagUpdated,
    FlagDeleted,
    SegmentUpdated,
    Sync,
    Ping
}

internal sealed class SseEvent
{
    public SseEventType Type { get; init; }
    public string? FlagKey { get; init; }
    public int? Version { get; init; }
    public GetFlagsResponse? Snapshot { get; init; }
}
