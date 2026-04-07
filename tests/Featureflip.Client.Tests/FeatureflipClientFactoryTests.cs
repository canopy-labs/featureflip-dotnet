using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>
/// Tests for the static factory that implements singleton-by-construction semantics.
/// These tests use FeatureflipClient.ResetForTesting() between scenarios to keep the
/// static map clean. Tests use distinct SDK keys per scenario to avoid cross-test
/// contamination when the xUnit runner uses parallel execution inside a class.
/// </summary>
[Collection("FactoryStaticState")] // serialize to avoid map contention
public class FeatureflipClientFactoryTests : IDisposable
{
    public FeatureflipClientFactoryTests()
    {
        FeatureflipClient.ResetForTesting();
    }

    public void Dispose()
    {
        FeatureflipClient.ResetForTesting();
    }

    [Fact]
    public void Get_FirstCall_ReturnsHandle()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        using var client = FeatureflipClient.Get("sdk-key-get-first", options);
        Assert.NotNull(client);
    }

    [Fact]
    public void Get_SameKeyTwice_ReturnsHandlesSharingOneCore()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        using var h1 = FeatureflipClient.Get("sdk-key-same", options);
        using var h2 = FeatureflipClient.Get("sdk-key-same", options);

        // Different handles
        Assert.NotSame(h1, h2);

        // But pointing at the same core — verify via the live-core-count diagnostic
        Assert.Equal(1, FeatureflipClient.DebugLiveCoreCount);
    }

    [Fact]
    public void Get_DifferentKeys_CreatesIndependentCores()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        using var h1 = FeatureflipClient.Get("sdk-key-a", options);
        using var h2 = FeatureflipClient.Get("sdk-key-b", options);

        Assert.Equal(2, FeatureflipClient.DebugLiveCoreCount);
    }

    [Fact]
    public void Get_AfterOnlyHandleDisposed_ConstructsNewCore()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        var h1 = FeatureflipClient.Get("sdk-key-recycle", options);
        h1.Dispose();

        Assert.Equal(0, FeatureflipClient.DebugLiveCoreCount);

        using var h2 = FeatureflipClient.Get("sdk-key-recycle", options);
        Assert.Equal(1, FeatureflipClient.DebugLiveCoreCount);
    }

    [Fact]
    public void Dispose_OneOfTwoHandles_KeepsCoreAlive()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        var h1 = FeatureflipClient.Get("sdk-key-twohandles", options);
        using var h2 = FeatureflipClient.Get("sdk-key-twohandles", options);

        h1.Dispose();

        // h2 is still usable; core is still alive
        Assert.Equal(1, FeatureflipClient.DebugLiveCoreCount);
    }

    [Fact]
    public void Get_NullOrEmptyKey_Throws()
    {
        Assert.Throws<FeatureFlagInitializationException>(() => FeatureflipClient.Get(""));
        Assert.Throws<FeatureFlagInitializationException>(() => FeatureflipClient.Get("  "));
    }

    [Fact]
    public async Task Get_ConcurrentSameKey_AllHandlesShareOneCore()
    {
        var options = new FeatureFlagOptions { StartOffline = true };
        const int threadCount = 32;
        var handles = new IFeatureflipClient[threadCount];

        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            handles[i] = FeatureflipClient.Get("sdk-key-concurrent", options);
        })).ToArray();

        await Task.WhenAll(tasks);

        try
        {
            // All 32 handles share exactly one core.
            Assert.Equal(1, FeatureflipClient.DebugLiveCoreCount);
            Assert.All(handles, Assert.NotNull);
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle?.Dispose();
            }
        }
    }
}
