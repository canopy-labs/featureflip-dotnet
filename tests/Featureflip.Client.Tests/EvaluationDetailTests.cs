using Xunit;

namespace Featureflip.Client.Tests;

public class EvaluationDetailTests
{
    [Fact]
    public void EvaluationDetail_StoresAllProperties()
    {
        var detail = new EvaluationDetail<bool>(
            value: true,
            reason: EvaluationReason.RuleMatch,
            ruleId: "rule-123",
            errorMessage: null
        );

        Assert.True(detail.Value);
        Assert.Equal(EvaluationReason.RuleMatch, detail.Reason);
        Assert.Equal("rule-123", detail.RuleId);
        Assert.Null(detail.ErrorMessage);
    }

    [Fact]
    public void EvaluationDetail_WithError_StoresErrorMessage()
    {
        var detail = new EvaluationDetail<string>(
            value: "default",
            reason: EvaluationReason.Error,
            ruleId: null,
            errorMessage: "Something went wrong"
        );

        Assert.Equal("default", detail.Value);
        Assert.Equal(EvaluationReason.Error, detail.Reason);
        Assert.Null(detail.RuleId);
        Assert.Equal("Something went wrong", detail.ErrorMessage);
    }
}
