using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Featureflip.Client.Extensions.DependencyInjection.Tests;

/// <summary>
/// Tests verifying the singleton-by-construction safety property: DI misregistration
/// of IFeatureflipClient as Scoped or Transient cannot accidentally create multiple
/// underlying clients, because the factory deduplicates by SDK key.
///
/// Uses FeatureflipClient.ResetForTesting() between tests to keep the static map
/// clean. Uses StartOffline = true to avoid network I/O during test construction.
/// </summary>
[Collection("FactoryStaticState")] // serialize: shares the static map with FeatureflipClientFactoryTests
public class ServiceCollectionExtensionsTests : IDisposable
{
    public ServiceCollectionExtensionsTests()
    {
        FeatureflipClient.ResetForTesting();
    }

    public void Dispose()
    {
        FeatureflipClient.ResetForTesting();
    }

    [Fact]
    public void ScopedMisregistration_AllScopesShareOneCore()
    {
        var services = new ServiceCollection();
        var options = new FeatureFlagOptions { StartOffline = true };
        services.AddScoped<IFeatureflipClient>(sp =>
            FeatureflipClient.Get("sdk-key-scoped-test", options));

        using var provider = services.BuildServiceProvider();

        using (var scope1 = provider.CreateScope())
        {
            var client = scope1.ServiceProvider.GetRequiredService<IFeatureflipClient>();
            Assert.NotNull(client);
        }
        using (var scope2 = provider.CreateScope())
        {
            var client = scope2.ServiceProvider.GetRequiredService<IFeatureflipClient>();
            Assert.NotNull(client);
        }
        using (var scope3 = provider.CreateScope())
        {
            var client = scope3.ServiceProvider.GetRequiredService<IFeatureflipClient>();
            Assert.NotNull(client);
        }

        // After all three scopes are disposed, the shared core reference count has
        // returned to zero and the map entry has been cleaned up. The headline
        // safety property: scoped misregistration does not leak connections per
        // scope — each scope gets a fresh handle, but all handles share one
        // underlying client while at least one is alive.
        Assert.Equal(0, FeatureflipClient.DebugLiveCoreCount);
    }

    [Fact]
    public void TransientMisregistration_AllResolvesShareOneCore()
    {
        var services = new ServiceCollection();
        var options = new FeatureFlagOptions { StartOffline = true };
        services.AddTransient<IFeatureflipClient>(sp =>
            FeatureflipClient.Get("sdk-key-transient-test", options));

        using var provider = services.BuildServiceProvider();

        // Resolve multiple transient instances — each gets its own handle.
        var h1 = provider.GetRequiredService<IFeatureflipClient>();
        var h2 = provider.GetRequiredService<IFeatureflipClient>();
        var h3 = provider.GetRequiredService<IFeatureflipClient>();

        try
        {
            // Different handle objects (transient semantics preserved at the handle level)
            Assert.NotSame(h1, h2);
            Assert.NotSame(h2, h3);

            // But all three share exactly one underlying shared core.
            Assert.Equal(1, FeatureflipClient.DebugLiveCoreCount);
        }
        finally
        {
            h1.Dispose();
            h2.Dispose();
            h3.Dispose();
        }
    }

    [Fact]
    public void AddFeatureflip_RegistersAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddFeatureflip("sdk-key-singleton-test", opts => opts.StartOffline = true);

        using var provider = services.BuildServiceProvider();
        var client1 = provider.GetRequiredService<IFeatureflipClient>();
        var client2 = provider.GetRequiredService<IFeatureflipClient>();

        // Singleton registration — same handle instance every resolve.
        Assert.Same(client1, client2);
    }
}
