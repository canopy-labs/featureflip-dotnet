using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class FlagEvaluatorTests
{
    private readonly FlagEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_FlagDisabled_ReturnsOffVariation()
    {
        // Arrange
        var flag = CreateBooleanFlag(enabled: false, offVariation: "off");
        var context = new EvaluationContext { UserId = "user1" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.FlagDisabled, result.Reason);
        Assert.Null(result.RuleId);
    }

    [Fact]
    public void Evaluate_NoRulesMatch_ReturnsFallthrough()
    {
        // Arrange
        var flag = CreateBooleanFlag(
            enabled: true,
            offVariation: "off",
            rules: new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new List<Condition>
                            {
                                new Condition
                                {
                                    Attribute = "country",
                                    Operator = ConditionOperator.Equals,
                                    Values = new List<string> { "US" }
                                }
                            }
                        }
                    },
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" }
                }
            },
            fallthrough: new ServeConfig { Type = ServeType.Fixed, Variation = "off" });
        var context = new EvaluationContext { UserId = "user1", Country = "UK" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        Assert.Null(result.RuleId);
    }

    [Fact]
    public void Evaluate_RuleMatches_ReturnsRuleVariation()
    {
        // Arrange
        var flag = CreateBooleanFlag(
            enabled: true,
            offVariation: "off",
            rules: new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new List<Condition>
                            {
                                new Condition
                                {
                                    Attribute = "country",
                                    Operator = ConditionOperator.Equals,
                                    Values = new List<string> { "US" }
                                }
                            }
                        }
                    },
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" }
                }
            },
            fallthrough: new ServeConfig { Type = ServeType.Fixed, Variation = "off" });
        var context = new EvaluationContext { UserId = "user1", Country = "US" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        Assert.Equal("on", result.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        Assert.Equal("rule1", result.RuleId);
    }

    [Fact]
    public void Evaluate_RuleWithSegmentKey_MatchesSegmentConditions()
    {
        // Arrange
        var segment = new Segment
        {
            Key = "beta-users",
            Version = 1,
            Conditions = new List<Condition>
            {
                new Condition
                {
                    Attribute = "plan",
                    Operator = ConditionOperator.Equals,
                    Values = new List<string> { "beta" }
                }
            },
            ConditionLogic = ConditionLogic.And
        };

        var flag = CreateBooleanFlag(
            enabled: true,
            offVariation: "off",
            rules: new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>(),
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" },
                    SegmentKey = "beta-users"
                }
            },
            fallthrough: new ServeConfig { Type = ServeType.Fixed, Variation = "off" });

        Func<string, Segment?> getSegment = key => key == "beta-users" ? segment : null;

        var matchingContext = new EvaluationContext { UserId = "user1" }.Set("plan", "beta");
        var nonMatchingContext = new EvaluationContext { UserId = "user2" }.Set("plan", "free");

        // Act
        var matchResult = _evaluator.Evaluate(flag, matchingContext, getSegment);
        var noMatchResult = _evaluator.Evaluate(flag, nonMatchingContext, getSegment);

        // Assert
        Assert.Equal("on", matchResult.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, matchResult.Reason);
        Assert.Equal("rule1", matchResult.RuleId);

        Assert.Equal("off", noMatchResult.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, noMatchResult.Reason);
    }

    [Fact]
    public void Evaluate_RuleWithMissingSegment_DoesNotMatch()
    {
        // Arrange
        var flag = CreateBooleanFlag(
            enabled: true,
            offVariation: "off",
            rules: new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>(),
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" },
                    SegmentKey = "nonexistent-segment"
                }
            },
            fallthrough: new ServeConfig { Type = ServeType.Fixed, Variation = "off" });

        Func<string, Segment?> getSegment = _ => null;
        var context = new EvaluationContext { UserId = "user1" };

        // Act
        var result = _evaluator.Evaluate(flag, context, getSegment);

        // Assert
        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
    }

    [Fact]
    public void Evaluate_RuleDoesNotMatch_TriesNextRule()
    {
        // Arrange
        var flag = CreateBooleanFlag(
            enabled: true,
            offVariation: "off",
            rules: new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new List<Condition>
                            {
                                new Condition
                                {
                                    Attribute = "country",
                                    Operator = ConditionOperator.Equals,
                                    Values = new List<string> { "US" }
                                }
                            }
                        }
                    },
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "variation1" }
                },
                new TargetingRule
                {
                    Id = "rule2",
                    Priority = 2,
                    ConditionGroups = new List<ConditionGroup>
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new List<Condition>
                            {
                                new Condition
                                {
                                    Attribute = "country",
                                    Operator = ConditionOperator.Equals,
                                    Values = new List<string> { "UK" }
                                }
                            }
                        }
                    },
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "variation2" }
                }
            },
            fallthrough: new ServeConfig { Type = ServeType.Fixed, Variation = "off" });
        var context = new EvaluationContext { UserId = "user1", Country = "UK" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        Assert.Equal("variation2", result.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        Assert.Equal("rule2", result.RuleId);
    }

    private static FlagConfiguration CreateBooleanFlag(
        bool enabled,
        string offVariation,
        List<TargetingRule>? rules = null,
        ServeConfig? fallthrough = null)
    {
        return new FlagConfiguration
        {
            Key = "test-flag",
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = rules ?? new List<TargetingRule>(),
            Fallthrough = fallthrough ?? new ServeConfig { Type = ServeType.Fixed, Variation = "on" },
            OffVariation = offVariation
        };
    }
}

public class FlagEvaluatorConditionTests
{
    private readonly FlagEvaluator _evaluator = new();

    [Theory]
    [InlineData("US", true)]
    [InlineData("us", true)]  // Case-insensitive
    [InlineData("CA", false)]
    public void Condition_Equals(string country, bool shouldMatch)
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "country",
            op: ConditionOperator.Equals,
            values: new List<string> { "US" }
        );
        var context = new EvaluationContext { UserId = "user1", Country = country };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    [Theory]
    [InlineData("alice@company.com", true)]
    [InlineData("bob@COMPANY.com", true)]  // Case-insensitive
    [InlineData("alice@other.com", false)]
    public void Condition_Contains(string email, bool shouldMatch)
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "email",
            op: ConditionOperator.Contains,
            values: new List<string> { "company.com" }
        );
        var context = new EvaluationContext { UserId = "user1", Email = email };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    [Theory]
    [InlineData("US", true)]
    [InlineData("us", false)]  // Case-sensitive (engine parity, RegexOptions.None)
    [InlineData("CA", false)]
    public void Condition_MatchesRegex_IsCaseSensitive(string country, bool shouldMatch)
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "country",
            op: ConditionOperator.MatchesRegex,
            values: new List<string> { "^US$" }
        );
        var context = new EvaluationContext { UserId = "user1", Country = country };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    [Fact]
    public void Condition_MatchesRegex_HonorsInlineCaseInsensitiveFlag()
    {
        // Case-insensitivity is opt-in via the (?i) inline flag in the pattern.
        var flag = CreateFlagWithCondition(
            attribute: "country",
            op: ConditionOperator.MatchesRegex,
            values: new List<string> { "(?i)^US$" }
        );
        var context = new EvaluationContext { UserId = "user1", Country = "us" };

        var result = _evaluator.Evaluate(flag, context);

        Assert.Equal("on", result.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
    }

    [Theory]
    [InlineData("admin_user", true)]
    [InlineData("ADMIN_test", true)]  // Case-insensitive
    [InlineData("user_admin", false)]
    public void Condition_StartsWith(string userId, bool shouldMatch)
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "userId",
            op: ConditionOperator.StartsWith,
            values: new List<string> { "admin" }
        );
        var context = new EvaluationContext { UserId = userId };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    [Theory]
    [InlineData("25", true)]
    [InlineData("18", false)]
    [InlineData("17", false)]
    public void Condition_GreaterThan(string age, bool shouldMatch)
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "age",
            op: ConditionOperator.GreaterThan,
            values: new List<string> { "18" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("age", age);

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    [Fact]
    public void Condition_Numeric_NonNumericOperand_DoesNotMatch()
    {
        // #1456: numeric operators must return no match when an operand isn't a parseable
        // number — mirroring the engine's double.TryParse behavior. The SDK previously fell
        // back to a lexical string compare, so these matched server-incompatibly:
        //   "gold" > "abc"  (lexical) and (float)-style coercion of "12abc"/"abc".
        AssertMatch(EvaluateNumericCondition("tier", ConditionOperator.GreaterThan, "abc", "gold"), shouldMatch: false);
        AssertMatch(EvaluateNumericCondition("level", ConditionOperator.GreaterThanOrEqual, "-5", "abc"), shouldMatch: false);
        AssertMatch(EvaluateNumericCondition("version", ConditionOperator.GreaterThan, "10", "12abc"), shouldMatch: false);
        // A non-numeric condition value contributes no match but doesn't break the others.
        AssertMatch(EvaluateNumericCondition("age", ConditionOperator.GreaterThan, "abc", "25", "18"), shouldMatch: true);
        // Sanity: a genuinely numeric comparison still matches.
        AssertMatch(EvaluateNumericCondition("age", ConditionOperator.GreaterThan, "18", "25"), shouldMatch: true);
    }

    private EvaluationResult EvaluateNumericCondition(
        string attribute,
        ConditionOperator op,
        string conditionValue,
        string contextValue,
        params string[] moreConditionValues)
    {
        var values = new List<string> { conditionValue };
        values.AddRange(moreConditionValues);
        var flag = CreateFlagWithCondition(attribute: attribute, op: op, values: values);
        var context = new EvaluationContext { UserId = "user1" }.Set(attribute, contextValue);
        return _evaluator.Evaluate(flag, context);
    }

    [Theory]
    // #1455: date operators must parse to UTC-normalized DateTimeOffset (mirroring the engine's
    // DateTimeOffset.TryParse with AssumeUniversal|AdjustToUniversal + a unix-seconds fallback),
    // and return NO match on unparseable input. The SDK previously used DateTime.TryParse with
    // DateTimeStyles.None (no TZ normalization, no unix fallback) and fell back to a lexical
    // string compare — so unparseable input wrongly matched and offset-bearing dates compared
    // by wall-clock rather than instant.

    // The operator is passed as its wire string (PascalCase) rather than the internal
    // ConditionOperator enum: xUnit theory methods must be public, and a public method can't
    // expose an internal type in its signature (CS0051). See packages/CLAUDE.md (#1430).

    // Timezone-offset normalization: 12:00+05:00 is 07:00Z, which is before 08:00Z.
    [InlineData("2026-01-01T12:00:00+05:00", "Before", "2026-01-01T08:00:00Z", true)]
    [InlineData("2026-01-01T12:00:00+05:00", "After", "2026-01-01T08:00:00Z", false)]
    // Unix-seconds fallback: 1700000000 == 2023-11-14T22:13:20Z.
    [InlineData("1700000000", "After", "2020-01-01T00:00:00Z", true)]
    [InlineData("1700000000", "Before", "2020-01-01T00:00:00Z", false)]
    // Unparseable input → NO match (previously matched via lexical string.Compare).
    [InlineData("hello", "Before", "world", false)]
    [InlineData("hello", "After", "world", false)]
    // No offset is assumed UTC.
    [InlineData("2026-01-01T08:00:00", "Before", "2026-01-01T09:00:00Z", true)]
    // Plain instant comparisons.
    [InlineData("2026-06-01T00:00:00Z", "After", "2026-01-01T00:00:00Z", true)]
    [InlineData("2026-06-01T00:00:00Z", "Before", "2026-01-01T00:00:00Z", false)]
    public void Condition_DateTime(string contextValue, string op, string conditionValue, bool shouldMatch)
    {
        var parsedOp = Enum.Parse<ConditionOperator>(op);
        AssertMatch(EvaluateDateCondition("ts", parsedOp, conditionValue, contextValue), shouldMatch);
    }

    [Fact]
    public void Condition_DateTime_MatchesAnyConditionValue()
    {
        // ANY-of: true when the comparison holds for at least one supplied value.
        AssertMatch(
            EvaluateDateCondition("ts", ConditionOperator.After, "2030-01-01T00:00:00Z", "2026-03-01T00:00:00Z", "2020-01-01T00:00:00Z"),
            shouldMatch: true);
        // Unparseable condition values are skipped; a later valid one can still match.
        AssertMatch(
            EvaluateDateCondition("ts", ConditionOperator.Before, "garbage", "2026-01-01T07:30:00Z", "2026-01-01T08:00:00Z"),
            shouldMatch: true);
        // A unix-seconds string is honored as a condition value too (1700000000 == 2023-11-14Z).
        AssertMatch(
            EvaluateDateCondition("ts", ConditionOperator.After, "1700000000", "2023-11-15T00:00:00Z"),
            shouldMatch: true);
    }

    private EvaluationResult EvaluateDateCondition(
        string attribute,
        ConditionOperator op,
        string conditionValue,
        string contextValue,
        params string[] moreConditionValues)
    {
        var values = new List<string> { conditionValue };
        values.AddRange(moreConditionValues);
        var flag = CreateFlagWithCondition(attribute: attribute, op: op, values: values);
        var context = new EvaluationContext { UserId = "user1" }.Set(attribute, contextValue);
        return _evaluator.Evaluate(flag, context);
    }

    [Theory]
    // Regression (#1409): "2.10.1" >= "2.0" must match. The decimal path failed to parse the
    // multi-segment string and silently returned the fallthrough.
    [InlineData("2.10.1", true)]
    [InlineData("2.0.0", true)]   // equal boundary is included
    [InlineData("2.0", true)]     // missing trailing segment == 2.0.0
    [InlineData("v2.5", true)]    // leading "v" tolerated
    [InlineData("1.9.9", false)]  // below the gate
    public void Condition_SemverGreaterThanOrEqual(string version, bool shouldMatch)
    {
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverGreaterThanOrEqual,
            values: new List<string> { "2.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", version);

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch);
    }

    [Theory]
    // Regression (#1409): "2.10" > "2.9" must be true under semver. Decimal comparison parsed
    // "2.10" as 2.1 and returned false.
    [InlineData("2.10", true)]
    [InlineData("2.9", false)]   // strict greater-than excludes equal
    [InlineData("2.9.1", true)]
    public void Condition_SemverGreaterThan(string version, bool shouldMatch)
    {
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverGreaterThan,
            values: new List<string> { "2.9" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", version);

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0", true)]      // missing trailing segment == 1.0.0
    [InlineData("v1.0.0", true)]   // leading "v" tolerated
    [InlineData("1.0.0+build", true)] // build metadata ignored
    [InlineData("1.0.1", false)]
    [InlineData("1.0.0-alpha", false)] // prerelease ranks below the release
    public void Condition_SemverEquals(string version, bool shouldMatch)
    {
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverEquals,
            values: new List<string> { "1.0.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", version);

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch);
    }

    [Theory]
    [InlineData("1.9.9", true)]
    [InlineData("2.0.0", false)]  // strict less-than excludes equal
    [InlineData("2.10.0", false)]
    public void Condition_SemverLessThan(string version, bool shouldMatch)
    {
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverLessThan,
            values: new List<string> { "2.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", version);

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch);
    }

    [Theory]
    [InlineData("2.0.0", true)]   // equal boundary is included
    [InlineData("1.0.0", true)]
    [InlineData("2.10.1", false)]
    public void Condition_SemverLessThanOrEqual(string version, bool shouldMatch)
    {
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverLessThanOrEqual,
            values: new List<string> { "2.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", version);

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch);
    }

    [Fact]
    public void Condition_Semver_UnparseableAttribute_DoesNotMatch()
    {
        // An unparseable version contributes no match, mirroring the numeric/date operators.
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverGreaterThanOrEqual,
            values: new List<string> { "1.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", "not-a-version");

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch: false);
    }

    [Fact]
    public void Condition_Semver_MatchesAnyConditionValue()
    {
        // Mirrors the numeric operators: true when the comparison holds for any supplied value.
        var flag = CreateFlagWithCondition(
            attribute: "version",
            op: ConditionOperator.SemverEquals,
            values: new List<string> { "1.0.0", "2.0.0" }
        );
        var context = new EvaluationContext { UserId = "user1" }.Set("version", "2.0");

        var result = _evaluator.Evaluate(flag, context);

        AssertMatch(result, shouldMatch: true);
    }

    private static void AssertMatch(EvaluationResult result, bool shouldMatch)
    {
        if (shouldMatch)
        {
            Assert.Equal("on", result.VariationKey);
            Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        }
        else
        {
            Assert.Equal("off", result.VariationKey);
            Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        }
    }

    // #1458: type-aware numeric coercion for Equals/NotEquals/In/NotIn. When the attribute
    // value is a boxed CLR numeric (int/long/double/etc. — NOT bool), equality operators
    // compare numerically, so 1.0 == "1" and 1 == "1.0". The literal is parsed with the same
    // strict, culture-invariant double.TryParse as the relational CompareNumeric helper, so
    // "1abc"/"abc" never match. Booleans and strings keep the existing string-compare path,
    // and only the four equality operators coerce — never Contains/StartsWith/EndsWith.

    [Fact]
    public void Condition_NumericEquals_DoubleAttribute_MatchesEquivalentLiterals()
    {
        AssertMatch(EvaluateNumericEqualityCondition(1.0, ConditionOperator.Equals, "1.0"), shouldMatch: true);
        AssertMatch(EvaluateNumericEqualityCondition(1.0, ConditionOperator.Equals, "1"), shouldMatch: true);
    }

    [Fact]
    public void Condition_NumericEquals_IntAttribute_MatchesEquivalentLiterals()
    {
        AssertMatch(EvaluateNumericEqualityCondition(1, ConditionOperator.Equals, "1.0"), shouldMatch: true);
        AssertMatch(EvaluateNumericEqualityCondition(1, ConditionOperator.Equals, "1"), shouldMatch: true);
    }

    [Fact]
    public void Condition_NumericEquals_FractionalValues()
    {
        AssertMatch(EvaluateNumericEqualityCondition(1.5, ConditionOperator.Equals, "1.5"), shouldMatch: true);
        AssertMatch(EvaluateNumericEqualityCondition(1.5, ConditionOperator.Equals, "1"), shouldMatch: false);
    }

    [Fact]
    public void Condition_NumericIn_MatchesAnyEquivalentLiteral()
    {
        AssertMatch(EvaluateNumericEqualityCondition(2, ConditionOperator.In, "1", "2.0"), shouldMatch: true);
        AssertMatch(EvaluateNumericEqualityCondition(3, ConditionOperator.In, "1", "2"), shouldMatch: false);
    }

    [Fact]
    public void Condition_NumericNotEquals_InvertsNumericMatch()
    {
        AssertMatch(EvaluateNumericEqualityCondition(1.0, ConditionOperator.NotEquals, "1.0"), shouldMatch: false);
        AssertMatch(EvaluateNumericEqualityCondition(1.0, ConditionOperator.NotEquals, "2"), shouldMatch: true);
    }

    [Fact]
    public void Condition_NumericNotIn_NoEquivalentLiteral_Matches()
    {
        AssertMatch(EvaluateNumericEqualityCondition(3, ConditionOperator.NotIn, "1", "2"), shouldMatch: true);
    }

    [Fact]
    public void Condition_NumericEquals_NonNumericLiteral_DoesNotMatch()
    {
        // Strict parse: a literal that isn't a clean number contributes no match.
        AssertMatch(EvaluateNumericEqualityCondition(1, ConditionOperator.Equals, "abc"), shouldMatch: false);
        AssertMatch(EvaluateNumericEqualityCondition(1, ConditionOperator.Equals, "1abc"), shouldMatch: false);
    }

    [Fact]
    public void Condition_BoolAttribute_IsNotNumeric()
    {
        // bool is deliberately excluded from numeric coercion: true != "1".
        AssertMatch(EvaluateNumericEqualityCondition(true, ConditionOperator.Equals, "1"), shouldMatch: false);
        // The existing string path still matches "true" == "True"/"true" (case-insensitive).
        AssertMatch(EvaluateNumericEqualityCondition(true, ConditionOperator.Equals, "true"), shouldMatch: true);
    }

    [Fact]
    public void Condition_StringAttribute_KeepsExactStringSemantics()
    {
        // A string attribute is NOT coerced: "1.0" stays a string, so it isn't equal to "1".
        AssertMatch(EvaluateNumericEqualityCondition("1.0", ConditionOperator.Equals, "1"), shouldMatch: false);
        // Leading-zero strings keep their textual identity: "01234" != "1234".
        AssertMatch(EvaluateNumericEqualityCondition("01234", ConditionOperator.Equals, "1234"), shouldMatch: false);
    }

    [Fact]
    public void Condition_NumericEquals_RespectsNegate()
    {
        // Negate inverts the numeric result: 1 Equals ["2"] is false, negated → true.
        AssertMatch(EvaluateNumericEqualityCondition(1, ConditionOperator.Equals, negate: true, "2"), shouldMatch: true);
    }

    private EvaluationResult EvaluateNumericEqualityCondition(
        object attributeValue,
        ConditionOperator op,
        params string[] values)
    {
        return EvaluateNumericEqualityCondition(attributeValue, op, negate: false, values);
    }

    private EvaluationResult EvaluateNumericEqualityCondition(
        object attributeValue,
        ConditionOperator op,
        bool negate,
        params string[] values)
    {
        var flag = CreateFlagWithCondition(
            attribute: "attr",
            op: op,
            values: new List<string>(values),
            negate: negate);
        var context = new EvaluationContext { UserId = "user1" }.Set("attr", attributeValue);
        return _evaluator.Evaluate(flag, context);
    }

    [Fact]
    public void Condition_Negate_InvertsResult()
    {
        // Arrange - condition matches US, but negate inverts it
        var flag = CreateFlagWithCondition(
            attribute: "country",
            op: ConditionOperator.Equals,
            values: new List<string> { "US" },
            negate: true
        );
        var contextUS = new EvaluationContext { UserId = "user1", Country = "US" };
        var contextUK = new EvaluationContext { UserId = "user1", Country = "UK" };

        // Act
        var resultUS = _evaluator.Evaluate(flag, contextUS);
        var resultUK = _evaluator.Evaluate(flag, contextUK);

        // Assert - US should NOT match (negated), UK SHOULD match (negated)
        Assert.Equal("off", resultUS.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, resultUS.Reason);

        Assert.Equal("on", resultUK.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, resultUK.Reason);
    }

    [Fact]
    public void Condition_MissingAttribute_ReturnsFalse()
    {
        // Arrange
        var flag = CreateFlagWithCondition(
            attribute: "nonexistent",
            op: ConditionOperator.Equals,
            values: new List<string> { "somevalue" }
        );
        var context = new EvaluationContext { UserId = "user1" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert - should not match when attribute is missing
        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
    }

    [Fact]
    public void Condition_MissingAttribute_WithEquals_EmptyValue_ShouldNotMatch()
    {
        // Arrange - attribute is missing, operator is Equals with value ""
        // Bug: null converts to "" which equals "" → incorrectly matches
        // Correct: missing attribute should short-circuit to false (condition.Negate)
        var flag = CreateFlagWithCondition(
            attribute: "nonexistent",
            op: ConditionOperator.Equals,
            values: new List<string> { "" }
        );
        var context = new EvaluationContext { UserId = "user1" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert - missing attribute should not match, even against empty string
        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
    }

    [Fact]
    public void Condition_MissingAttribute_WithNegate_ContainsEmpty_ShouldMatch()
    {
        // Arrange - attribute missing, Contains("") with negate=true
        // Bug: null→"", "".Contains("")→true, !true→false (wrong)
        // Correct: missing attribute → return condition.Negate (true) → rule matches
        var flag = CreateFlagWithCondition(
            attribute: "nonexistent",
            op: ConditionOperator.Contains,
            values: new List<string> { "" },
            negate: true
        );
        var context = new EvaluationContext { UserId = "user1" };

        // Act
        var result = _evaluator.Evaluate(flag, context);

        // Assert
        Assert.Equal("on", result.VariationKey);
        Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
    }

    private static FlagConfiguration CreateFlagWithCondition(
        string attribute,
        ConditionOperator op,
        List<string> values,
        bool negate = false)
    {
        return new FlagConfiguration
        {
            Key = "test-flag",
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule1",
                    Priority = 1,
                    ConditionGroups = new List<ConditionGroup>
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new List<Condition>
                            {
                                new Condition
                                {
                                    Attribute = attribute,
                                    Operator = op,
                                    Values = values,
                                    Negate = negate
                                }
                            }
                        }
                    },
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" }
                }
            },
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "off" },
            OffVariation = "off"
        };
    }
}

public class FlagEvaluatorRolloutTests
{
    private readonly FlagEvaluator _evaluator = new();

    [Fact]
    public void Rollout_NoVariations_ServesDefaultVariation()
    {
        // Env-level PercentageRollout emits a Rollout serve with its default variation set but
        // no weighted variations (no per-variation weight storage at the env level, #1469). The
        // evaluator degrades to the default variation instead of dereferencing an empty/null
        // Variations list. Regression lock — the guard already exists in the SDK evaluator.
        var flag = new FlagConfiguration
        {
            Key = "rollout-flag",
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig
            {
                Type = ServeType.Rollout,
                BucketBy = "userId",
                Variation = "off",
                Variations = new List<WeightedVariation>()
            },
            OffVariation = "off"
        };
        var context = new EvaluationContext { UserId = "user-1" };

        var result = _evaluator.Evaluate(flag, context);

        Assert.Equal("off", result.VariationKey);
    }

    [Fact]
    public void Rollout_SameUserGetsSameVariation()
    {
        // Arrange
        var flag = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            }
        );
        var context = new EvaluationContext { UserId = "consistent-user-123" };

        // Act - Evaluate same user 10 times
        var results = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            results.Add(_evaluator.Evaluate(flag, context).VariationKey);
        }

        // Assert - All results should be the same
        Assert.All(results, r => Assert.Equal(results[0], r));
    }

    [Fact]
    public void Rollout_DifferentUsersGetDistribution()
    {
        // Arrange
        var flag = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            }
        );

        // Act - Evaluate 1000 different users
        var onCount = 0;
        var offCount = 0;
        for (int i = 0; i < 1000; i++)
        {
            var context = new EvaluationContext { UserId = $"user-{i}" };
            var result = _evaluator.Evaluate(flag, context);
            if (result.VariationKey == "on")
                onCount++;
            else
                offCount++;
        }

        // Assert - Distribution should be roughly 50/50 (within 350-650 range)
        Assert.InRange(onCount, 350, 650);
        Assert.InRange(offCount, 350, 650);
    }

    [Fact]
    public void Rollout_100Percent_AlwaysReturnsVariation()
    {
        // Arrange
        var flag = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 100 },
                new WeightedVariation { Key = "off", Weight = 0 }
            }
        );

        // Act - Evaluate 100 different users
        var results = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var context = new EvaluationContext { UserId = $"user-{i}" };
            results.Add(_evaluator.Evaluate(flag, context).VariationKey);
        }

        // Assert - All results should be "on"
        Assert.All(results, r => Assert.Equal("on", r));
    }

    [Fact]
    public void Rollout_CustomBucketBy_UsesSpecifiedAttribute()
    {
        // Arrange - bucket by email instead of userId
        var flag = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            },
            bucketBy: "email"
        );
        var sameEmail = "test@example.com";
        var context1 = new EvaluationContext { UserId = "user-1", Email = sameEmail };
        var context2 = new EvaluationContext { UserId = "user-2", Email = sameEmail };

        // Act
        var result1 = _evaluator.Evaluate(flag, context1);
        var result2 = _evaluator.Evaluate(flag, context2);

        // Assert - Same email, different userId should get same result
        Assert.Equal(result1.VariationKey, result2.VariationKey);
    }

    [Fact]
    public void Rollout_DefaultBucketBy_MatchesExplicitUserId()
    {
        // Arrange - the default bucketBy (null) should behave identically
        // to explicitly setting bucketBy to "userId"
        var flagDefault = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            },
            bucketBy: null  // uses default
        );
        var flagExplicit = CreateRolloutFlag(
            variations: new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            },
            bucketBy: "userId"  // explicit camelCase
        );
        var context = new EvaluationContext { UserId = "test-user-123" };

        // Act
        var resultDefault = _evaluator.Evaluate(flagDefault, context);
        var resultExplicit = _evaluator.Evaluate(flagExplicit, context);

        // Assert - default bucketBy should be "userId", same as explicit
        Assert.Equal(resultExplicit.VariationKey, resultDefault.VariationKey);
    }

    [Fact]
    public void Rollout_KeylessContext_ServesControlVariationDeterministically()
    {
        // Arrange - keyless/anonymous context can't be bucketed. The thin control
        // weight (1) ensures the old empty-hash collapse would NOT land on "on",
        // so this fails pre-fix. After the fix, a keyless userId rollout serves the
        // control (first) variation deterministically (#1457).
        var flag = new FlagConfiguration
        {
            Key = "rollout-flag",
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig
            {
                Type = ServeType.Rollout,
                BucketBy = "userId",
                Salt = "test-salt",
                Variations = new List<WeightedVariation>
                {
                    new WeightedVariation { Key = "on", Weight = 1 },
                    new WeightedVariation { Key = "off", Weight = 99 }
                }
            },
            OffVariation = "off"
        };

        // keyless/anonymous context: no UserId set
        var context = new EvaluationContext();

        // Act - evaluate the keyless context many times
        var results = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            results.Add(_evaluator.Evaluate(flag, context).VariationKey);
        }

        // Assert - always the control (first) variation, stable across evals
        Assert.All(results, r => Assert.Equal("on", r));
    }

    private static FlagConfiguration RolloutFlagWithSalt(string? salt) => new FlagConfiguration
    {
        Key = "rollout-flag",
        Version = 1,
        Type = FlagType.Boolean,
        Enabled = true,
        Variations = new List<Variation>
        {
            new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
            new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
        },
        Rules = new List<TargetingRule>(),
        Fallthrough = new ServeConfig
        {
            Type = ServeType.Rollout,
            BucketBy = "userId",
            Salt = salt,
            Variations = new List<WeightedVariation>
            {
                new WeightedVariation { Key = "on", Weight = 50 },
                new WeightedVariation { Key = "off", Weight = 50 }
            }
        },
        OffVariation = "off"
    };

    [Fact]
    public void Rollout_NullSalt_BucketsLikeEmptySalt_NotFlagKey()
    {
        // A null salt must hash as "" (engine behavior), not the flag key.
        // Pre-fix: null -> flagKey "rollout-flag" -> different bucket for some users -> FAIL.
        var nullSalt = RolloutFlagWithSalt(null);
        var emptySalt = RolloutFlagWithSalt("");

        for (var i = 0; i < 50; i++)
        {
            var ctx = new EvaluationContext { UserId = $"user-{i}" };
            Assert.Equal(
                _evaluator.Evaluate(emptySalt, ctx).VariationKey,
                _evaluator.Evaluate(nullSalt, ctx).VariationKey);
        }
    }

    private static FlagConfiguration CreateRolloutFlag(
        List<WeightedVariation> variations,
        string? bucketBy = null)
    {
        return new FlagConfiguration
        {
            Key = "rollout-flag",
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig
            {
                Type = ServeType.Rollout,
                BucketBy = bucketBy,
                Variations = variations
            },
            OffVariation = "off"
        };
    }
}
