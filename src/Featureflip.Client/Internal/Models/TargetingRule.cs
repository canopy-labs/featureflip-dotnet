namespace Featureflip.Client.Internal.Models;

internal sealed record ConditionGroup
{
    public ConditionLogic Operator { get; set; }
    public List<Condition> Conditions { get; set; } = new();
}

internal sealed record TargetingRule
{
    public string Id { get; set; } = string.Empty;
    public int Priority { get; set; }
    public List<ConditionGroup> ConditionGroups { get; set; } = new();
    public ServeConfig? Serve { get; set; }
    public string? SegmentKey { get; set; }
}
