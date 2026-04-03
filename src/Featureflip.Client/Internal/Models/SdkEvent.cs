using System.Text.Json;

namespace Featureflip.Client.Internal.Models;

internal sealed record SdkEvent
{
    public string Type { get; init; } = string.Empty;
    public string FlagKey { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string? Variation { get; init; }
    public DateTime Timestamp { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
