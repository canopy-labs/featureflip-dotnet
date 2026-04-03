namespace Featureflip.Client.Internal.Models;

internal sealed record FlagConfiguration
{
    public string Key { get; init; } = string.Empty;
    public int Version { get; init; }
    public FlagType Type { get; init; }
    public bool Enabled { get; init; }
    public List<Variation> Variations { get; init; } = new();
    public List<TargetingRule> Rules { get; init; } = new();
    public ServeConfig? Fallthrough { get; init; }
    public string OffVariation { get; init; } = string.Empty;
}
