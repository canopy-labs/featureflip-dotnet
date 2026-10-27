namespace Featureflip.Client.Internal.Models;

internal sealed record ServeConfig
{
    public ServeType Type { get; set; }
    public string? Variation { get; set; }
    public string? BucketBy { get; set; }
    public string? Salt { get; set; }
    public List<WeightedVariation>? Variations { get; set; }
}

internal sealed record WeightedVariation
{
    public string Key { get; set; } = string.Empty;
    public int Weight { get; set; }
}
