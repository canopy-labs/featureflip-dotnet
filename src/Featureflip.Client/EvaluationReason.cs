namespace Featureflip.Client;

/// <summary>
/// Indicates why a particular variation was returned.
/// </summary>
public enum EvaluationReason
{
    /// <summary>A targeting rule matched.</summary>
    RuleMatch = 0,

    /// <summary>No rules matched, used fallthrough configuration.</summary>
    Fallthrough = 1,

    /// <summary>Flag is disabled, returned off variation.</summary>
    FlagDisabled = 2,

    /// <summary>Flag does not exist, returned default value.</summary>
    FlagNotFound = 3,

    /// <summary>An error occurred during evaluation, returned default value.</summary>
    Error = 4
}
