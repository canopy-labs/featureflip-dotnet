namespace Featureflip.Client.Internal.Models;

internal sealed record FlagConfiguration
{
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public FlagType Type { get; set; }
    public bool Enabled { get; set; }
    public List<Variation> Variations { get; set; } = new();
    public List<TargetingRule> Rules { get; set; } = new();
    public ServeConfig? Fallthrough { get; set; }
    public string OffVariation { get; set; } = string.Empty;
    public List<Prerequisite> Prerequisites { get; set; } = new();
}
