namespace Featureflip.Client.Internal.Models;

internal sealed record Condition
{
    public string Attribute { get; set; } = string.Empty;
    public ConditionOperator Operator { get; set; }
    public List<string> Values { get; set; } = new();
    public bool Negate { get; set; }
}
