using System.Text.Json;
using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>Wire shape of the reason/ruleId fields on evaluation events (#4268).</summary>
// Same collection as FeatureflipClientFactoryTests: this class builds real clients through
// FeatureflipClient.Get, whose core map is process-wide. Run in parallel, that class's
// ResetForTesting() force-shut these cores mid-test (the evaluation was then dropped and the
// flush found no events), and these cores inflated its DebugLiveCoreCount assertion.
[Collection("FactoryStaticState")]
public class EvaluationEventReasonTests
{
    [Fact]
    public async Task ErrorRead_DoesNotCarryRuleId()
    {
        // A String flag whose rule serves it, read through the boolean accessor: the
        // evaluation matched a rule but the typed read fails, so reason is error and the
        // rule id must not be sent.
        const string flags = """
        {"environment":"test","version":1,"flags":[{"key":"s","version":1,"type":"String","enabled":true,
          "variations":[{"key":"a","value":"hello"},{"key":"b","value":"bye"}],
          "rules":[{"id":"rule-1","priority":0,"conditionGroups":[],"serve":{"type":"Fixed","variation":"a"},"segmentKey":null}],
          "fallthrough":{"type":"Fixed","variation":"b"},"offVariation":"b","prerequisites":[]}],"segments":[]}
        """;

        await using var server = new EventCaptureServer(flags);
        using var client = server.CreateClient();

        var detail = client.VariationDetail("s", new EvaluationContext { UserId = "u1" }, false);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
        await client.FlushAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(server.CapturedEvents());
        Assert.Equal("error", evt.GetProperty("reason").GetString());
        Assert.False(evt.TryGetProperty("ruleId", out _), evt.GetRawText());
    }
}
