namespace Featureflip.Client;

/// <summary>
/// A single flag-evaluation observation handed to every registered
/// <see cref="EvaluationInspector"/>. Fired synchronously, in-process, once per
/// variation call — on every exit path (success, flag-not-found, error).
/// </summary>
/// <remarks>
/// <para>
/// This is the frozen cross-SDK inspector contract; the property names mirror the
/// JS reference implementation's <c>EvaluationEvent</c> fields (adapted to .NET
/// PascalCase). <see cref="Reason"/> uses this SDK's native
/// <see cref="EvaluationReason"/> enum rather than the JS string form.
/// </para>
/// <para>
/// Binary compatibility: new fields must be added as an additional constructor
/// overload, with the shorter signature delegating forward — adding a parameter to
/// an existing public constructor (even an optional one) is binary-breaking.
/// </para>
/// </remarks>
public sealed class EvaluationEvent
{
    /// <summary>The key of the flag that was evaluated.</summary>
    public string FlagKey { get; }

    /// <summary>
    /// The full evaluation context. This is a <em>copy</em> of the caller's context —
    /// a misbehaving inspector cannot mutate the object the caller passed in.
    /// </summary>
    public EvaluationContext Context { get; }

    /// <summary>The value the caller actually receives (the default value, when one was applied).</summary>
    public object? Value { get; }

    /// <summary>The winning variation key. Null when the flag was not found or evaluation errored.</summary>
    public string? VariationKey { get; }

    /// <summary>Why this value was served.</summary>
    public EvaluationReason Reason { get; }

    /// <summary>
    /// The id of the rule that matched. Set only when <see cref="Reason"/> is
    /// <see cref="EvaluationReason.RuleMatch"/>.
    /// </summary>
    public string? RuleId { get; }

    /// <summary>
    /// The key of the prerequisite flag that failed. Set only when <see cref="Reason"/>
    /// is <see cref="EvaluationReason.PrerequisiteFailed"/>.
    /// </summary>
    public string? PrerequisiteKey { get; }

    /// <summary>The time the evaluation was observed, as an ISO-8601 (round-trip) string.</summary>
    public string Timestamp { get; }

    /// <summary>Creates an evaluation event. Constructed by the SDK; not intended for user code.</summary>
    public EvaluationEvent(
        string flagKey,
        EvaluationContext context,
        object? value,
        string? variationKey,
        EvaluationReason reason,
        string? ruleId,
        string? prerequisiteKey,
        string timestamp)
    {
        FlagKey = flagKey;
        Context = context;
        Value = value;
        VariationKey = variationKey;
        Reason = reason;
        RuleId = ruleId;
        PrerequisiteKey = prerequisiteKey;
        Timestamp = timestamp;
    }
}
