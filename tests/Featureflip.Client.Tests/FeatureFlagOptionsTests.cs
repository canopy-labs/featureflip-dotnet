using Xunit;

namespace Featureflip.Client.Tests;

public class FeatureFlagOptionsTests
{
    [Fact]
    public void FeatureFlagOptions_HasCorrectDefaults()
    {
        var options = new FeatureFlagOptions();

        Assert.Equal("https://eval.featureflip.io", options.BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ReadTimeout);
        Assert.True(options.Streaming);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.FlushInterval);
        Assert.Equal(100, options.FlushBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(10), options.InitTimeout);
    }

    [Fact]
    public void FeatureFlagOptions_CanBeCustomized()
    {
        var options = new FeatureFlagOptions
        {
            BaseUrl = "https://custom.example.com",
            Streaming = false,
            PollInterval = TimeSpan.FromMinutes(1),
            FlushBatchSize = 50
        };

        Assert.Equal("https://custom.example.com", options.BaseUrl);
        Assert.False(options.Streaming);
        Assert.Equal(TimeSpan.FromMinutes(1), options.PollInterval);
        Assert.Equal(50, options.FlushBatchSize);
    }
}
