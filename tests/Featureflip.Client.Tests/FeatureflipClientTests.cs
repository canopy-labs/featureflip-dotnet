using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests;

public class FeatureflipClientTests
{
    [Fact]
    public void BoolVariation_FlagNotFound_ReturnsDefault()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.BoolVariation("nonexistent", context, true);

        Assert.True(result);
    }

    [Fact]
    public void BoolVariation_FlagNotFound_ReturnsDefaultFalse()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.BoolVariation("nonexistent", context, false);

        Assert.False(result);
    }

    [Fact]
    public void BoolVariation_FlagExists_ReturnsEvaluatedValue()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateBooleanFlag("my-flag", true, "on")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.BoolVariation("my-flag", context, false);

        Assert.True(result);
    }

    [Fact]
    public void BoolVariation_FlagDisabled_ReturnsOffVariation()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateBooleanFlag("my-flag", false, "off")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.BoolVariation("my-flag", context, true);

        Assert.False(result);
    }

    [Fact]
    public void StringVariation_ReturnsCorrectType()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateStringFlag("string-flag", true, "on", "hello-world")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.StringVariation("string-flag", context, "default");

        Assert.Equal("hello-world", result);
    }

    [Fact]
    public void StringVariation_FlagNotFound_ReturnsDefault()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.StringVariation("nonexistent", context, "default-value");

        Assert.Equal("default-value", result);
    }

    [Fact]
    public void IntVariation_ReturnsCorrectType()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateIntFlag("int-flag", true, "on", 42)
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.IntVariation("int-flag", context, 0);

        Assert.Equal(42, result);
    }

    [Fact]
    public void IntVariation_FlagNotFound_ReturnsDefault()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.IntVariation("nonexistent", context, 99);

        Assert.Equal(99, result);
    }

    [Fact]
    public void DoubleVariation_ReturnsCorrectType()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateDoubleFlag("double-flag", true, "on", 3.14)
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.DoubleVariation("double-flag", context, 0.0);

        Assert.Equal(3.14, result, precision: 2);
    }

    [Fact]
    public void DoubleVariation_FlagNotFound_ReturnsDefault()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.DoubleVariation("nonexistent", context, 1.5);

        Assert.Equal(1.5, result, precision: 2);
    }

    [Fact]
    public void JsonVariation_ReturnsDeserializedObject()
    {
        var config = new TestConfig { Name = "test", Value = 123 };
        var flags = new List<FlagConfiguration>
        {
            CreateJsonFlag("json-flag", true, "on", config)
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.JsonVariation("json-flag", context, new TestConfig());

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
        Assert.Equal(123, result.Value);
    }

    [Fact]
    public void JsonVariation_FlagNotFound_ReturnsDefault()
    {
        var defaultConfig = new TestConfig { Name = "default", Value = 0 };
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.JsonVariation("nonexistent", context, defaultConfig);

        Assert.Equal("default", result.Name);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void VariationDetail_ReturnsReasonInformation()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateBooleanFlag("my-flag", true, "on")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.VariationDetail("my-flag", context, false);

        Assert.True(result.Value);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("on", result.VariationKey);
    }

    [Fact]
    public void VariationDetail_FlagNotFound_ReturnsDefaultWithReason()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.VariationDetail("nonexistent", context, true);

        Assert.True(result.Value);
        Assert.Equal(EvaluationReason.FlagNotFound, result.Reason);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.VariationKey);
    }

    [Fact]
    public void VariationDetail_FlagDisabled_ReturnsOffVariationWithReason()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateBooleanFlag("disabled-flag", false, "off")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.VariationDetail("disabled-flag", context, true);

        Assert.False(result.Value);
        Assert.Equal(EvaluationReason.FlagDisabled, result.Reason);
        Assert.Equal("off", result.VariationKey);
    }

    [Fact]
    public void Variation_GenericBool_ReturnsCorrectValue()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateBooleanFlag("bool-flag", true, "on")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.Variation("bool-flag", context, false);

        Assert.True(result);
    }

    [Fact]
    public void Variation_GenericString_ReturnsCorrectValue()
    {
        var flags = new List<FlagConfiguration>
        {
            CreateStringFlag("string-flag", true, "on", "hello")
        };
        var client = CreateTestClient(flags);
        var context = new EvaluationContext { UserId = "user-123" };

        var result = client.Variation("string-flag", context, "default");

        Assert.Equal("hello", result);
    }

    [Fact]
    public void IsInitialized_InternalConstructor_ReturnsTrue()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());

        Assert.True(client.IsInitialized);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());

        // Should not throw
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());

        // Should not throw
        client.Flush();
    }

    [Fact]
    public async Task FlushAsync_DoesNotThrow()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());

        // Should not throw
        await client.FlushAsync();
    }

    [Fact]
    public async Task ConcurrentDispose_DoesNotThrow()
    {
        var client = CreateTestClient(new List<FlagConfiguration>());
        var context = new EvaluationContext { UserId = "user-123" };

        // Run multiple Dispose calls and Flush calls concurrently
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() => client.Dispose()));
            tasks.Add(Task.Run(() => client.Flush()));
            tasks.Add(Task.Run(() => client.BoolVariation("flag", context, false)));
        }

        // Should not throw ObjectDisposedException or any other exception
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void BoolVariation_RuleMatch_ReturnsRuleVariation()
    {
        var flag = CreateBooleanFlag("rule-flag", true, "on", new List<TargetingRule>
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
                Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "off" }
            }
        });

        var client = CreateTestClient(new List<FlagConfiguration> { flag });
        var context = new EvaluationContext { UserId = "user-123", Country = "US" };

        var result = client.BoolVariation("rule-flag", context, true);

        Assert.False(result);
    }

    [Fact]
    public void VariationDetail_RuleMatch_ReturnsRuleId()
    {
        var flag = CreateBooleanFlag("rule-flag", true, "on", new List<TargetingRule>
        {
            new TargetingRule
            {
                Id = "my-rule-id",
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
        });

        var client = CreateTestClient(new List<FlagConfiguration> { flag });
        var context = new EvaluationContext { UserId = "user-123", Country = "US" };

        var result = client.VariationDetail("rule-flag", context, false);

        Assert.True(result.Value);
        Assert.Equal(EvaluationReason.RuleMatch, result.Reason);
        Assert.Equal("my-rule-id", result.RuleId);
    }

    private static FeatureflipClient CreateTestClient(List<FlagConfiguration> flags)
    {
        var store = new FlagStore();
        store.Replace(flags, new List<Segment>());
        return new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
    }

    private static FlagConfiguration CreateBooleanFlag(
        string key,
        bool enabled,
        string fallthroughVariation,
        List<TargetingRule>? rules = null)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = rules ?? new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthroughVariation },
            OffVariation = "off"
        };
    }

    private static FlagConfiguration CreateStringFlag(string key, bool enabled, string fallthroughVariation, string value)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.String,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(value) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement("off-value") }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthroughVariation },
            OffVariation = "off"
        };
    }

    private static FlagConfiguration CreateIntFlag(string key, bool enabled, string fallthroughVariation, int value)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Number,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(value) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(0) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthroughVariation },
            OffVariation = "off"
        };
    }

    private static FlagConfiguration CreateDoubleFlag(string key, bool enabled, string fallthroughVariation, double value)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Number,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(value) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(0.0) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthroughVariation },
            OffVariation = "off"
        };
    }

    private static FlagConfiguration CreateJsonFlag<T>(string key, bool enabled, string fallthroughVariation, T value)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Json,
            Enabled = enabled,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(value) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement<object?>(null) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthroughVariation },
            OffVariation = "off"
        };
    }

    private class TestConfig
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
