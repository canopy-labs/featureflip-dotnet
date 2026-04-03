using System.Text.Json;

namespace Featureflip.Client.Internal.Models;

internal sealed record Variation
{
    public string Key { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
}
