namespace Featureflip.Client;

/// <summary>
/// Exception thrown when the feature flag client fails to initialize.
/// </summary>
public class FeatureFlagInitializationException : Exception
{
    public FeatureFlagInitializationException(string message) : base(message)
    {
    }

    public FeatureFlagInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
