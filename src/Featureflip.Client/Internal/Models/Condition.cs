namespace Featureflip.Client.Internal.Models;

internal sealed record Condition
{
    public string Attribute { get; init; } = string.Empty;
    public ConditionOperator Operator { get; init; }
    public List<string> Values { get; init; } = new();
    public bool Negate { get; init; }
}
