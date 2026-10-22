using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>Canonical spelling of floating bucket values and unknown served variations (#4236).</summary>
public class CanonicalNumberTests
{
    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(1.5, "1.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(-0.0, "0")]
    [InlineData(100.25, "100.25")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1.5e21, "1.5e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(2.5e-7, "2.5e-7")]
    [InlineData(123456.789, "123456.789")]
    [InlineData(-42.5, "-42.5")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(5.684341886080802e-14, "5.684341886080802e-14")]
    [InlineData(1e23, "1e+23")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    public void Double_matches_ecmascript_spelling(double x, string expected)
        => Assert.Equal(expected, CanonicalNumber.Format(x));

    [Fact]
    public void Float_uses_shortest_single_precision_spelling()
    {
        Assert.Equal("0.1", CanonicalNumber.Format(0.1f));
        Assert.Equal("1", CanonicalNumber.Format(1.0f));
    }

    [Fact]
    public void Decimal_bucket_value_hashes_as_its_canonical_text()
    {
        // Integral decimals keep their exact digits; others go through double. Compared across
        // many salts so a 50/50 coincidence cannot pass for a match.
        var cases = new (decimal Value, string Text)[]
        {
            (1.0m, "1"), (100m, "100"), (1E+2m, "100"), (-5.00m, "-5"), (-0.0m, "0"),
            (12345678901234567890m, "12345678901234567890"),
            (2.50m, "2.5"), (0.1m, "0.1"),
        };
        foreach (var (value, text) in cases)
        {
            for (var i = 0; i < 20; i++)
            {
                var serve = new ServeConfig
                {
                    Type = ServeType.Rollout, BucketBy = "id", Salt = $"s{i}",
                    Variations = new List<WeightedVariation>
                    {
                        new() { Key = "a", Weight = 50 }, new() { Key = "b", Weight = 50 }
                    }
                };
                var flag = new FlagConfiguration
                {
                    Key = "f", Enabled = true,
                    Variations = new List<Variation>
                    {
                        new() { Key = "a", Value = System.Text.Json.JsonSerializer.SerializeToElement("va") },
                        new() { Key = "b", Value = System.Text.Json.JsonSerializer.SerializeToElement("vb") },
                    },
                    Rules = new List<TargetingRule>(),
                    Fallthrough = serve, OffVariation = "a"
                };
                var viaDecimal = new FlagEvaluator().Evaluate(flag, new EvaluationContext().Set("id", value), null, null);
                var viaText = new FlagEvaluator().Evaluate(flag, new EvaluationContext().Set("id", text), null, null);
                Assert.Equal(viaText.VariationKey, viaDecimal.VariationKey);
            }
        }
    }

    private static FlagConfiguration Flag(ServeConfig rule, ServeConfig fallthrough) => new()
    {
        Key = "f",
        Enabled = true,
        Variations = new List<Variation> { new() { Key = "a", Value = System.Text.Json.JsonSerializer.SerializeToElement("va") } },
        Rules = new List<TargetingRule>
        {
            new() { Id = "r1", Priority = 0, Serve = rule, ConditionGroups = new() }
        },
        Fallthrough = fallthrough,
        OffVariation = "a"
    };

    [Fact]
    public void Rule_serving_unknown_variation_is_error_without_rule_id()
    {
        var flag = Flag(new ServeConfig { Type = ServeType.Fixed, Variation = "nope" },
            new ServeConfig { Type = ServeType.Fixed, Variation = "a" });
        var r = new FlagEvaluator().Evaluate(flag, new EvaluationContext { UserId = "u" }, null, null);
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal("nope", r.VariationKey);
        Assert.Null(r.RuleId);
    }

    [Fact]
    public void Fallthrough_serving_unknown_variation_is_error()
    {
        var flag = Flag(new ServeConfig { Type = ServeType.Fixed, Variation = "a" },
            new ServeConfig { Type = ServeType.Fixed, Variation = "nope" });
        flag = flag with { Rules = new List<TargetingRule>() };
        var r = new FlagEvaluator().Evaluate(flag, new EvaluationContext { UserId = "u" }, null, null);
        Assert.Equal(EvaluationReason.Error, r.Reason);
        Assert.Equal("nope", r.VariationKey);
    }
}
