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
