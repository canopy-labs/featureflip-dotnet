namespace Featureflip.Client;

/// <summary>
/// Options for configuring the feature flag client via dependency injection.
/// Extends <see cref="FeatureFlagOptions"/> with the SDK key for configuration binding.
/// </summary>
public sealed class FeatureflipClientOptions : FeatureFlagOptions
{
    /// <summary>The SDK key for authentication.</summary>
    public string SdkKey { get; set; } = string.Empty;
}
