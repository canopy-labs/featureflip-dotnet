namespace Featureflip.Client.Internal.Models;

internal sealed record Prerequisite
{
    public string PrerequisiteFlagKey { get; init; } = string.Empty;
    public string ExpectedVariationKey { get; init; } = string.Empty;
}
