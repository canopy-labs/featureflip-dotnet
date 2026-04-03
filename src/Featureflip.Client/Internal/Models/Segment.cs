namespace Featureflip.Client.Internal.Models;

internal sealed record Segment
{
    public string Key { get; init; } = string.Empty;
    public int Version { get; init; }
    public List<Condition> Conditions { get; init; } = new();
    public ConditionLogic ConditionLogic { get; init; }
}
