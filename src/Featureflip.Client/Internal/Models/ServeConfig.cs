namespace Featureflip.Client.Internal.Models;

internal sealed record ServeConfig
{
    public ServeType Type { get; init; }
    public string? Variation { get; init; }
    public string? BucketBy { get; init; }
    public string? Salt { get; init; }
    public List<WeightedVariation>? Variations { get; init; }
}

internal sealed record WeightedVariation
{
    public string Key { get; init; } = string.Empty;
    public int Weight { get; init; }
}
