using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

/// <summary>
/// Covers the <c>onEvaluation</c> inspector contract (issue #1914) as ported from
/// <c>packages/js-sdk/tests/inspector.test.ts</c>. Inspectors fire once per variation
/// call on every exit path of the evaluation choke point, with the reason and value
/// the caller actually receives.
/// </summary>
public class InspectorTests
{
    // ---------------------------------------------------------------------
    // Exit path: success (fallthrough)
    // ---------------------------------------------------------------------

    [Fact]
    public void Inspector_FiresOnceWithFullPayload_OnFallthrough()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => events.Add(e)));
        var context = new EvaluationContext { UserId = "user-1", Country = "US" };

        var value = client.BoolVariation("my-flag", context, defaultValue: false);

        Assert.True(value);
        var evt = Assert.Single(events);
        Assert.Equal("my-flag", evt.FlagKey);
        Assert.Equal(true, evt.Value);
        Assert.Equal("on", evt.VariationKey);
        Assert.Equal(EvaluationReason.Fallthrough, evt.Reason);
        Assert.Null(evt.RuleId);
        Assert.Null(evt.PrerequisiteKey);
        Assert.Equal("user-1", evt.Context.UserId);
        AssertIso8601(evt.Timestamp);
    }

    [Fact]
    public void Inspector_FiresOncePerVariationCall()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => events.Add(e)));
        var context = new EvaluationContext { UserId = "user-1" };

        client.BoolVariation("my-flag", context, defaultValue: false);
        Assert.Single(events);

        client.BoolVariation("my-flag", context, defaultValue: false);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Inspector_FiresFromVariationDetail_Too()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => events.Add(e)));

        var detail = client.VariationDetail("my-flag", new EvaluationContext { UserId = "user-1" }, defaultValue: false);

        var evt = Assert.Single(events);
        Assert.Equal(detail.Value, evt.Value);
        Assert.Equal(detail.Reason, evt.Reason);
        Assert.Equal(detail.VariationKey, evt.VariationKey);
    }

    [Fact]
    public void Inspector_FiresWithFlagDisabledReason()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(new List<FlagConfiguration> { BoolFlag("my-flag", enabled: false, fallthrough: "on") },
            Inspectors(e => events.Add(e)));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "user-1" }, defaultValue: true);

        Assert.False(value);
        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.FlagDisabled, evt.Reason);
        Assert.Equal("off", evt.VariationKey);
        Assert.Equal(false, evt.Value);
    }

    // ---------------------------------------------------------------------
    // Exit path: success (rule match / prerequisite failure)
    // ---------------------------------------------------------------------

    [Fact]
    public void Inspector_FiresWithRuleId_OnRuleMatch()
    {
        var events = new List<EvaluationEvent>();
        var flag = BoolFlag("my-flag", enabled: true, fallthrough: "off") with
        {
            Rules = new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = "rule-42",
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
            }
        };
        var client = CreateClient(new List<FlagConfiguration> { flag }, Inspectors(e => events.Add(e)));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u", Country = "US" }, defaultValue: false);

        Assert.True(value);
        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.RuleMatch, evt.Reason);
        Assert.Equal("rule-42", evt.RuleId);
        Assert.Equal("on", evt.VariationKey);
        Assert.Null(evt.PrerequisiteKey);
    }

    [Fact]
    public void Inspector_FiresWithPrerequisiteKey_OnPrerequisiteFailure()
    {
        var events = new List<EvaluationEvent>();
        var parent = BoolFlag("parent", enabled: true, fallthrough: "on");
        var child = BoolFlag("child", enabled: true, fallthrough: "on") with
        {
            Prerequisites = new List<Prerequisite>
            {
                new Prerequisite { PrerequisiteFlagKey = "parent", ExpectedVariationKey = "off" }
            }
        };
        var client = CreateClient(new List<FlagConfiguration> { parent, child }, Inspectors(e => events.Add(e)));

        var value = client.BoolVariation("child", new EvaluationContext { UserId = "u" }, defaultValue: true);

        Assert.False(value);
        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.PrerequisiteFailed, evt.Reason);
        Assert.Equal("parent", evt.PrerequisiteKey);
        Assert.Equal("off", evt.VariationKey);
        Assert.Null(evt.RuleId);
    }

    // ---------------------------------------------------------------------
    // Exit path: flag not found
    // ---------------------------------------------------------------------

    [Fact]
    public void Inspector_FiresWithDefaultValue_OnFlagNotFound()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(new List<FlagConfiguration>(), Inspectors(e => events.Add(e)));

        var value = client.StringVariation("missing", new EvaluationContext { UserId = "u" }, defaultValue: "fallback");

        Assert.Equal("fallback", value);
        var evt = Assert.Single(events);
        Assert.Equal("missing", evt.FlagKey);
        Assert.Equal("fallback", evt.Value);
        Assert.Equal(EvaluationReason.FlagNotFound, evt.Reason);
        Assert.Null(evt.VariationKey);
        Assert.Null(evt.RuleId);
        Assert.Null(evt.PrerequisiteKey);
        AssertIso8601(evt.Timestamp);
    }

    // ---------------------------------------------------------------------
    // Exit path: error
    // ---------------------------------------------------------------------

    [Fact]
    public void Inspector_FiresWithDefaultValue_WhenServedVariationIsUndefined()
    {
        // The evaluator resolves a variation key the flag doesn't define -> Error.
        var events = new List<EvaluationEvent>();
        var flag = BoolFlag("my-flag", enabled: true, fallthrough: "does-not-exist");
        var client = CreateClient(new List<FlagConfiguration> { flag }, Inspectors(e => events.Add(e)));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: true);

        Assert.True(value);
        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.Error, evt.Reason);
        Assert.Equal(true, evt.Value);
        Assert.Null(evt.VariationKey);
        Assert.Null(evt.RuleId);
        Assert.Null(evt.PrerequisiteKey);
    }

    [Fact]
    public void VariationDetail_ReportsError_WhenServedVariationIsUndefined()
    {
        // The returned detail (what the caller sees) reports Error rather than the
        // misleading Fallthrough the evaluator resolved for the since-deleted key,
        // keeping the attempted variation key for diagnostics (#1989).
        var flag = BoolFlag("my-flag", enabled: true, fallthrough: "does-not-exist");
        var client = CreateClient(new List<FlagConfiguration> { flag }, Inspectors());

        var detail = client.VariationDetail("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.Equal(EvaluationReason.Error, detail.Reason);
        Assert.False(detail.Value);
        Assert.Equal("does-not-exist", detail.VariationKey);
    }

    [Fact]
    public void Inspector_FiresOnce_WhenEvaluationThrows()
    {
        // A malformed flag (null rule list) makes the evaluator throw, exercising the
        // choke point's catch path. The inspector must still fire exactly once.
        var events = new List<EvaluationEvent>();
        var flag = BoolFlag("my-flag", enabled: true, fallthrough: "on") with { Rules = null! };
        var client = CreateClient(new List<FlagConfiguration> { flag }, Inspectors(e => events.Add(e)));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: true);

        Assert.True(value);
        var evt = Assert.Single(events);
        Assert.Equal(EvaluationReason.Error, evt.Reason);
        Assert.Equal(true, evt.Value);
        Assert.Null(evt.VariationKey);
    }

    // ---------------------------------------------------------------------
    // Multiple / throwing / null / absent inspectors
    // ---------------------------------------------------------------------

    [Fact]
    public void MultipleInspectors_AllFire()
    {
        var first = new List<EvaluationEvent>();
        var second = new List<EvaluationEvent>();
        var third = new List<EvaluationEvent>();
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => first.Add(e), e => second.Add(e), e => third.Add(e)));

        client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Single(third);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void ThrowingInspector_DoesNotChangeValue_AndDoesNotStopSiblings()
    {
        var before = new List<EvaluationEvent>();
        var after = new List<EvaluationEvent>();
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(
                e => before.Add(e),
                _ => throw new InvalidOperationException("boom"),
                e => after.Add(e)));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.True(value, "a throwing inspector must not change the value the caller receives");
        Assert.Single(before);
        Assert.Single(after);
    }

    [Fact]
    public void ThrowingInspector_LogsWarning()
    {
        var logger = new CapturingLogger();
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") }, new List<Segment>());
        var options = new FeatureFlagOptions
        {
            Inspectors = Inspectors(_ => throw new InvalidOperationException("boom"))
        };
        using var core = SharedFeatureflipCore.CreateForTesting(store, new FlagEvaluator(), options, logger);

        var detail = core.Evaluate("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.True(detail.Value);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("inspector", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullInspectorEntries_AreFilteredOut()
    {
        var events = new List<EvaluationEvent>();
        var inspectors = new List<EvaluationInspector> { null!, e => events.Add(e), null! };
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            inspectors);

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.True(value);
        Assert.Single(events);
    }

    [Fact]
    public void OnlyNullInspectorEntries_IsANoOp()
    {
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            new List<EvaluationInspector> { null! });

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.True(value);
    }

    [Fact]
    public void NoInspectorsConfigured_IsANoOp()
    {
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            inspectors: null);

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);
        var missing = client.BoolVariation("nope", new EvaluationContext { UserId = "u" }, defaultValue: true);

        Assert.True(value);
        Assert.True(missing);
    }

    [Fact]
    public void FeatureFlagOptions_DefaultsToNoInspectors()
    {
        Assert.Empty(new FeatureFlagOptions().Inspectors);
    }

    // ---------------------------------------------------------------------
    // Closed client
    // ---------------------------------------------------------------------

    [Fact]
    public void Inspector_DoesNotFire_AfterTheClientIsDisposed()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => events.Add(e)));
        var context = new EvaluationContext { UserId = "u" };

        Assert.True(client.BoolVariation("my-flag", context, defaultValue: false));
        Assert.Single(events);

        client.Dispose();

        // A closed client emits no inspector events, but still returns the same value:
        // disposal suppresses the notification only, it does not change evaluation.
        var value = client.BoolVariation("my-flag", context, defaultValue: false);

        Assert.True(value, "disposal must not change the value a caller receives");
        Assert.Single(events);
    }

    [Fact]
    public void Inspector_DoesNotFire_AfterTheCoreIsDisposed_OnEveryExitPath()
    {
        var events = new List<EvaluationEvent>();
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            new List<Segment>());
        var options = new FeatureFlagOptions { Inspectors = Inspectors(e => events.Add(e)) };
        var core = SharedFeatureflipCore.CreateForTesting(store, new FlagEvaluator(), options);

        core.Dispose();
        Assert.True(core.IsShutDown);

        var served = core.Evaluate("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);
        var missing = core.Evaluate("nope", new EvaluationContext { UserId = "u" }, defaultValue: true);

        Assert.True(served.Value);
        Assert.Equal(EvaluationReason.Fallthrough, served.Reason);
        Assert.True(missing.Value);
        Assert.Equal(EvaluationReason.FlagNotFound, missing.Reason);
        Assert.Empty(events);
    }

    // ---------------------------------------------------------------------
    // Context copy
    // ---------------------------------------------------------------------

    [Fact]
    public void EventContext_IsACopy_MutatingItDoesNotAffectTheCaller()
    {
        var events = new List<EvaluationEvent>();
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            Inspectors(e => events.Add(e)));
        var context = new EvaluationContext { UserId = "original", Country = "US" }.Set("plan", "pro");

        client.BoolVariation("my-flag", context, defaultValue: false);

        var evt = Assert.Single(events);
        Assert.NotSame(context, evt.Context);
        Assert.Equal("original", evt.Context.UserId);
        Assert.Equal("US", evt.Context.Country);
        Assert.Equal("pro", evt.Context.GetAttribute("plan"));

        // A buggy inspector mutating the event's context must not touch the caller's.
        evt.Context.UserId = "tampered";
        evt.Context.Country = "GB";
        evt.Context.Set("plan", "free");
        evt.Context.Set("injected", "yes");

        Assert.Equal("original", context.UserId);
        Assert.Equal("US", context.Country);
        Assert.Equal("pro", context.GetAttribute("plan"));
        Assert.Null(context.GetAttribute("injected"));
    }

    [Fact]
    public void ConfiguredInspectorList_IsSnapshotAtConstruction()
    {
        var events = new List<EvaluationEvent>();
        var configured = Inspectors(e => events.Add(e));
        var client = CreateClient(
            new List<FlagConfiguration> { BoolFlag("my-flag", enabled: true, fallthrough: "on") },
            configured);

        // Mutating the caller's collection after construction has no effect: the core
        // froze its own copy, which is what makes the read path lock-free.
        configured.Clear();
        configured.Add(_ => throw new InvalidOperationException("never registered"));

        var value = client.BoolVariation("my-flag", new EvaluationContext { UserId = "u" }, defaultValue: false);

        Assert.True(value);
        Assert.Single(events);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static void AssertIso8601(string timestamp)
    {
        Assert.False(string.IsNullOrWhiteSpace(timestamp));
        Assert.EndsWith("Z", timestamp);
        Assert.True(
            DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _),
            $"timestamp '{timestamp}' is not a parseable ISO-8601 value");
    }

    private static IList<EvaluationInspector> Inspectors(params EvaluationInspector[] inspectors)
        => new List<EvaluationInspector>(inspectors);

    private static FeatureflipClient CreateClient(
        List<FlagConfiguration> flags,
        IList<EvaluationInspector>? inspectors)
    {
        var store = new FlagStore();
        store.Replace(flags, new List<Segment>());
        var options = new FeatureFlagOptions();
        if (inspectors != null)
        {
            options.Inspectors = inspectors;
        }
        return new FeatureflipClient(store, new FlagEvaluator(), options);
    }

    private static FlagConfiguration BoolFlag(string key, bool enabled, string fallthrough)
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
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = fallthrough },
            OffVariation = "off"
        };
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
