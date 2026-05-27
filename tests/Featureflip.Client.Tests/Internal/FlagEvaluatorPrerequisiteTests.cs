using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class FlagEvaluatorPrerequisiteTests
{
    private readonly FlagEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_NoPrerequisites_BehavesUnchanged()
    {
        // Regression: flags without prerequisites must continue to evaluate exactly as before.
        var flag = BoolFlag("child", enabled: true);
        var allFlags = ToMap(flag);

        var result = _evaluator.Evaluate(flag, new EvaluationContext { UserId = "u1" }, allFlags);

        Assert.Equal("on", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        Assert.Null(result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_PrerequisiteSatisfied_EvaluatesChildNormally()
    {
        var parent = BoolFlag("parent", enabled: true); // serves "on" via fallthrough
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(parent, child));

        Assert.Equal("on", result.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, result.Reason);
        Assert.Null(result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_PrerequisiteUnsatisfied_ReturnsOffVariationWithPrerequisiteKey()
    {
        var parent = BoolFlag("parent", enabled: true); // serves "on"
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "off" } // mismatch
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(parent, child));

        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("parent", result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_DisabledPrerequisite_ServesOffSoMismatchFails()
    {
        var parent = BoolFlag("parent", enabled: false); // disabled => serves offVariation "off"
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(parent, child));

        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("parent", result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_MultiplePrerequisites_FirstFailingKeyIsReported()
    {
        var p1 = BoolFlag("p1", enabled: true); // "on"
        var p2 = BoolFlag("p2", enabled: true); // "on"
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "p1", ExpectedVariationKey = "off" }, // first fails
            new Prerequisite { PrerequisiteFlagKey = "p2", ExpectedVariationKey = "off" }, // also fails — not reported
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(p1, p2, child));

        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("p1", result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_ChainedPrerequisites_PropagatesFailureUpward()
    {
        // grandparent -> parent (depends on grandparent) -> child (depends on parent)
        var grandparent = BoolFlag("grandparent", enabled: true); // serves "on"
        var parent = BoolFlag("parent", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "grandparent", ExpectedVariationKey = "off" } // fails
        });
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(grandparent, parent, child));

        // parent evaluates to "off" (its prereq failed) — that doesn't match child's expected "on"
        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("parent", result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_MissingPrerequisiteFlag_TreatedAsPrerequisiteFailed()
    {
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "ghost", ExpectedVariationKey = "on" }
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, ToMap(child));

        Assert.Equal("off", result.VariationKey);
        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("ghost", result.PrerequisiteKey);
    }

    [Fact]
    public void Evaluate_DepthExceeded_ReturnsErrorReason()
    {
        // Build a chain of 12 flags: f0 -> f1 -> ... -> f11. MaxPrerequisiteDepth is 10,
        // so f0 should hit the error path.
        var flags = new Dictionary<string, FlagConfiguration>(StringComparer.Ordinal);
        for (var i = 0; i < 12; i++)
        {
            Prerequisite[]? prereqs = i < 11
                ? new[] { new Prerequisite { PrerequisiteFlagKey = $"f{i + 1}", ExpectedVariationKey = "on" } }
                : null;
            var f = BoolFlag($"f{i}", enabled: true, prerequisites: prereqs);
            flags[f.Key] = f;
        }

        var result = _evaluator.Evaluate(flags["f0"], new EvaluationContext { UserId = "u1" }, flags);

        Assert.Equal(EvaluationReason.Error, result.Reason);
    }

    [Fact]
    public void Evaluate_NullAllFlagsWithPrerequisite_TreatedAsMissing()
    {
        var child = BoolFlag("child", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });

        var result = _evaluator.Evaluate(child, new EvaluationContext { UserId = "u1" }, allFlags: null);

        Assert.Equal(EvaluationReason.PrerequisiteFailed, result.Reason);
        Assert.Equal("parent", result.PrerequisiteKey);
    }

    [Fact]
    public void EvaluateWithSharedMemo_ReusesPrerequisiteResultAcrossFlags()
    {
        // Two child flags share a prerequisite. With a shared memo, the prereq should
        // only evaluate once across both top-level calls.
        var prereqEvaluations = 0;
        // Use a parent flag with a rule that increments a counter via a side-effect-bearing condition.
        // Simpler: use a custom flag whose evaluation we can observe by inspecting memo state.
        var parent = BoolFlag("parent", enabled: true);
        var childA = BoolFlag("a", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });
        var childB = BoolFlag("b", enabled: true, prerequisites: new[]
        {
            new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "on" }
        });
        var allFlags = ToMap(parent, childA, childB);
        var memo = new Dictionary<string, EvaluationResult>(StringComparer.Ordinal);
        var ctx = new EvaluationContext { UserId = "u1" };

        var a = _evaluator.EvaluateWithSharedMemo(childA, ctx, allFlags, getSegment: null, memo);
        // After first call, parent must be cached in memo.
        Assert.True(memo.ContainsKey("parent"));
        var parentResultAfterFirst = memo["parent"];

        var b = _evaluator.EvaluateWithSharedMemo(childB, ctx, allFlags, getSegment: null, memo);

        // Same reference: memo wasn't overwritten with a fresh evaluation for parent.
        Assert.Same(parentResultAfterFirst, memo["parent"]);
        Assert.Equal("on", a.VariationKey);
        Assert.Equal("on", b.VariationKey);

        _ = prereqEvaluations; // silence unused
    }

    private static IReadOnlyDictionary<string, FlagConfiguration> ToMap(params FlagConfiguration[] flags)
    {
        var d = new Dictionary<string, FlagConfiguration>(StringComparer.Ordinal);
        foreach (var f in flags) d[f.Key] = f;
        return d;
    }

    private static FlagConfiguration BoolFlag(
        string key,
        bool enabled,
        IEnumerable<Prerequisite>? prerequisites = null)
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
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) },
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "on" },
            OffVariation = "off",
            Prerequisites = prerequisites?.ToList() ?? new List<Prerequisite>(),
        };
    }
}
