namespace Featureflip.Client.Internal.Models;

internal sealed record Segment
{
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<Condition> Conditions { get; set; } = new();
    public ConditionLogic ConditionLogic { get; set; }
}
