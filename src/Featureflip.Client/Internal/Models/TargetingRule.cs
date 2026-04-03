namespace Featureflip.Client.Internal.Models;

internal sealed record ConditionGroup
{
    public ConditionLogic Operator { get; init; }
    public List<Condition> Conditions { get; init; } = new();
}

internal sealed record TargetingRule
{
    public string Id { get; init; } = string.Empty;
    public int Priority { get; init; }
    public List<ConditionGroup> ConditionGroups { get; init; } = new();
    public ServeConfig? Serve { get; init; }
    public string? SegmentKey { get; init; }
}
