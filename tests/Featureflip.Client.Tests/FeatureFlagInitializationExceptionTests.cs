using Xunit;

namespace Featureflip.Client.Tests;

public class FeatureFlagInitializationExceptionTests
{
    [Fact]
    public void FeatureFlagInitializationException_WithMessage()
    {
        var ex = new FeatureFlagInitializationException("Failed to connect");

        Assert.Equal("Failed to connect", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void FeatureFlagInitializationException_WithInnerException()
    {
        var inner = new TimeoutException("Timed out");
        var ex = new FeatureFlagInitializationException("Init failed", inner);

        Assert.Equal("Init failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
