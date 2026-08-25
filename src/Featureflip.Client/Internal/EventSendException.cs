namespace Featureflip.Client.Internal;

/// <summary>
/// Thrown when the events endpoint answers a flush with a non-success status.
/// </summary>
/// <remarks>
/// The flush path has to tell a retryable failure (5xx, 429) from a permanent one (other
/// 4xx) so it knows whether to keep the batch (#2456), and it must do so on every target
/// framework — <c>HttpRequestException.StatusCode</c> exists on net5+ but not on
/// netstandard2.0, so the status is carried here instead. The property is deliberately
/// <em>not</em> called <c>StatusCode</c>: that would hide the base member where it exists.
/// Derives from <see cref="HttpRequestException"/> so callers that catch only that — the
/// type <c>EnsureSuccessStatusCode</c> used to throw here — keep working unchanged.
/// </remarks>
internal sealed class EventSendException : HttpRequestException
{
    public int ResponseStatusCode { get; }

    /// <summary>
    /// True for statuses where the same batch could succeed if sent again: any 5xx (the
    /// production edge answers this endpoint with a 503 at a low constant rate — #2456) and
    /// 429, where the server is explicitly asking the caller to come back later.
    /// </summary>
    public bool IsRetryable => ResponseStatusCode >= 500 || ResponseStatusCode == 429;

    public EventSendException(int statusCode)
        : base($"Events endpoint responded {statusCode}.")
    {
        ResponseStatusCode = statusCode;
    }
}
