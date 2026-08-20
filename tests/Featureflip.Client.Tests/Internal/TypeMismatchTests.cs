using System.Collections.Generic;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// A typed accessor whose served value is not of the requested type must hand back the
/// caller's default AND report <see cref="EvaluationReason.Error"/>, so the mismatch is
/// detectable (#2281). C# already returned the default — its strict
/// <c>JsonElement</c> accessors throw — but the throw was swallowed inside
/// <c>DeserializeValue</c>, so the caller still saw the evaluator's success reason and
/// had no signal that anything had gone wrong.
/// </summary>
public class TypeMismatchTests
{
    [Fact]
    public void BoolFlagReadAsString_ReturnsDefaultWithError()
    {
        var client = CreateClient(BoolFlag("bool-flag"));

        var detail = client.VariationDetail("bool-flag", Context(), "DEF");

        Assert.Equal("DEF", detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void BoolFlagReadAsInt_ReturnsDefaultWithError()
    {
        var client = CreateClient(BoolFlag("bool-flag"));

        var detail = client.VariationDetail("bool-flag", Context(), -1);

        Assert.Equal(-1, detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void StringFlagReadAsInt_ReturnsDefaultWithError()
    {
        var client = CreateClient(ValueFlag("str-flag", FlagType.String, JsonSerializer.SerializeToElement("42")));

        var detail = client.VariationDetail("str-flag", Context(), -1);

        Assert.Equal(-1, detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void StringFlagReadAsBool_ReturnsDefaultWithError()
    {
        var client = CreateClient(ValueFlag("str-flag", FlagType.String, JsonSerializer.SerializeToElement("42")));

        var detail = client.VariationDetail("str-flag", Context(), true);

        Assert.True(detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void FractionalNumberReadAsInt_ReturnsDefaultWithError()
    {
        var client = CreateClient(ValueFlag("num-flag", FlagType.Number, JsonSerializer.SerializeToElement(42.5)));

        var detail = client.VariationDetail("num-flag", Context(), -1);

        Assert.Equal(-1, detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void JsonNullReadAsString_ReturnsDefaultWithError()
    {
        var client = CreateClient(ValueFlag("null-flag", FlagType.String, JsonSerializer.SerializeToElement((string?)null)));

        var detail = client.VariationDetail("null-flag", Context(), "DEF");

        Assert.Equal("DEF", detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
    }

    [Fact]
    public void TypeMismatch_ReportsErrorToInspectorsWithTheCallersDefault()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(BoolFlag("bool-flag"), e => events.Add(e));

        client.IntVariation("bool-flag", Context(), defaultValue: -1);

        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.Error, evt.Reason);
        Assert.Equal(-1, evt.Value);
        // EvaluationEvent documents the variation key as null when evaluation
        // errored, the same shape the malformed-variation path uses (#1989).
        Assert.Null(evt.VariationKey);
    }

    // --- Matching reads must be untouched by the strictness change ---

    [Fact]
    public void MatchingTypes_StillServeTheValueWithSuccessReason()
    {
        var client = CreateClient(
            BoolFlag("bool-flag"),
            null,
            ValueFlag("str-flag", FlagType.String, JsonSerializer.SerializeToElement("hello")),
            ValueFlag("int-flag", FlagType.Number, JsonSerializer.SerializeToElement(42)));

        var ctx = Context();

        var boolDetail = client.VariationDetail("bool-flag", ctx, false);
        Assert.False(boolDetail.Value);
        Assert.Equal(EvaluationReason.Fallthrough, boolDetail.Reason);

        Assert.Equal("hello", client.StringVariation("str-flag", ctx, "DEF"));
        Assert.Equal(42, client.IntVariation("int-flag", ctx, -1));

        // A whole JSON number satisfies a double read, as GetDouble() always has.
        Assert.Equal(42.0, client.DoubleVariation("int-flag", ctx, -1.0));
    }

    private static EvaluationContext Context() => new EvaluationContext { UserId = "user-1" };

    private static FeatureflipClient CreateClient(
        FlagConfiguration flag,
        EvaluationInspector? inspector = null,
        params FlagConfiguration[] more)
    {
        var flags = new List<FlagConfiguration> { flag };
        flags.AddRange(more);

        var store = new FlagStore();
        store.Replace(flags, new List<Segment>());
        var options = new FeatureFlagOptions();
        if (inspector != null)
        {
            options.Inspectors = new List<EvaluationInspector> { inspector };
        }
        return new FeatureflipClient(store, new FlagEvaluator(), options);
    }

    /// <summary>An enabled boolean flag whose fallthrough serves "off" (false).</summary>
    private static FlagConfiguration BoolFlag(string key)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "off" },
            OffVariation = "off"
        };
    }

    /// <summary>An enabled flag with a single variation "v" serving <paramref name="value"/>.</summary>
    private static FlagConfiguration ValueFlag(string key, FlagType type, JsonElement value)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = 1,
            Type = type,
            Enabled = true,
            Variations = new List<Variation> { new Variation { Key = "v", Value = value } },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "v" },
            OffVariation = "v"
        };
    }
}
