namespace Featureflip.Client;

/// <summary>
/// Contains the result of a flag evaluation with additional diagnostic information.
/// </summary>
/// <typeparam name="T">The type of the flag value.</typeparam>
public sealed class EvaluationDetail<T>
{
    /// <summary>The evaluated value.</summary>
    public T Value { get; }

    /// <summary>The reason this value was returned.</summary>
    public EvaluationReason Reason { get; }

    /// <summary>The ID of the rule that matched, if applicable.</summary>
    public string? RuleId { get; }

    /// <summary>The key of the matched variation, if applicable.</summary>
    public string? VariationKey { get; }

    /// <summary>Error message if evaluation failed.</summary>
    public string? ErrorMessage { get; }

    public EvaluationDetail(T value, EvaluationReason reason, string? ruleId, string? errorMessage)
        : this(value, reason, ruleId, errorMessage, variationKey: null)
    {
    }

    public EvaluationDetail(T value, EvaluationReason reason, string? ruleId, string? errorMessage, string? variationKey)
    {
        Value = value;
        Reason = reason;
        RuleId = ruleId;
        ErrorMessage = errorMessage;
        VariationKey = variationKey;
    }
}
