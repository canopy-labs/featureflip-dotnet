namespace Featureflip.Client.Internal.Models;

internal enum FlagType
{
    Boolean,
    String,
    Number,
    Json
}

internal enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    In,
    NotIn,
    MatchesRegex,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Before,
    After
}

internal enum ConditionLogic
{
    And,
    Or
}

internal enum ServeType
{
    Fixed,
    Rollout
}
