namespace Featureflip.Client.Internal.Models;

internal sealed record Prerequisite
{
    public string PrerequisiteFlagKey { get; set; } = string.Empty;
    public string ExpectedVariationKey { get; set; } = string.Empty;
}
