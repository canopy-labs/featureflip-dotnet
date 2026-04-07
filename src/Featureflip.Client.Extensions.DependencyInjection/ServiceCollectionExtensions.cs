using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client;

/// <summary>
/// Extension methods for adding Featureflip to the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Featureflip client to the service collection with the specified SDK key.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="sdkKey">The SDK key for authentication.</param>
    /// <param name="configure">Optional action to configure additional options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFeatureflip(
        this IServiceCollection services,
        string sdkKey,
        Action<FeatureFlagOptions>? configure = null)
    {
        var options = new FeatureFlagOptions();
        configure?.Invoke(options);

        services.AddSingleton<IFeatureflipClient>(sp =>
        {
            var logger = sp.GetService<ILogger<FeatureflipClient>>();
            return FeatureflipClient.Get(sdkKey, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the Featureflip client to the service collection from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section containing Featureflip settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SdkKey is not configured.</exception>
    public static IServiceCollection AddFeatureflip(
        this IServiceCollection services,
        IConfigurationSection configuration)
    {
        var clientOptions = new FeatureflipClientOptions();
        configuration.Bind(clientOptions);

        if (string.IsNullOrEmpty(clientOptions.SdkKey))
        {
            throw new InvalidOperationException("SdkKey is required in configuration");
        }

        services.AddSingleton<IFeatureflipClient>(sp =>
        {
            var logger = sp.GetService<ILogger<FeatureflipClient>>();
            return FeatureflipClient.Get(clientOptions.SdkKey, clientOptions, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the Featureflip client to the service collection with configuration action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure client options including SDK key.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SdkKey is not configured.</exception>
    public static IServiceCollection AddFeatureflip(
        this IServiceCollection services,
        Action<FeatureflipClientOptions> configure)
    {
        var options = new FeatureflipClientOptions();
        configure(options);

        if (string.IsNullOrEmpty(options.SdkKey))
        {
            throw new InvalidOperationException("SdkKey is required");
        }

        services.AddSingleton<IFeatureflipClient>(sp =>
        {
            var logger = sp.GetService<ILogger<FeatureflipClient>>();
            return FeatureflipClient.Get(options.SdkKey, options, logger);
        });

        return services;
    }
}
