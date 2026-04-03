using Xunit;

namespace Featureflip.Client.Tests;

public class EvaluationReasonTests
{
    [Fact]
    public void EvaluationReason_HasExpectedValues()
    {
        // Verify all expected enum values exist
        Assert.Equal(0, (int)EvaluationReason.RuleMatch);
        Assert.Equal(1, (int)EvaluationReason.Fallthrough);
        Assert.Equal(2, (int)EvaluationReason.FlagDisabled);
        Assert.Equal(3, (int)EvaluationReason.FlagNotFound);
        Assert.Equal(4, (int)EvaluationReason.Error);
    }
}
