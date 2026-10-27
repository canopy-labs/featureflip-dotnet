namespace Featureflip.Client.Internal.Models;

/// <summary>Body of <c>POST /v1/sdk/events</c>.</summary>
/// <remarks>
/// A top-level type rather than a private nested one so the source-generated
/// <see cref="FeatureflipJsonContext"/> can reference it (#4301).
/// </remarks>
internal sealed class SendEventsRequest
{
    public IReadOnlyList<SdkEvent> Events { get; set; } = Array.Empty<SdkEvent>();
}
