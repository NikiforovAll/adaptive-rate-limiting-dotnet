namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// What a response says about the rate.
/// </summary>
/// <param name="StatusCode">
/// The status code of the response, or of a throttled inner response. A server error (500 or
/// more) that is not a throttle does not change the rate.
/// </param>
/// <param name="RetryAfter">How long the server asks the client to wait, if it says.</param>
/// <param name="ThrottledCount">
/// The number of throttled responses: 0 for none, 1 for a throttled response, or more for a batch
/// with several throttled inner responses.
/// </param>
public readonly record struct ThrottleSignal(
    int StatusCode,
    TimeSpan? RetryAfter = null,
    int ThrottledCount = 0
)
{
    /// <summary>
    /// True if the server throttled the request.
    /// </summary>
    public bool IsThrottled => ThrottledCount > 0;
}
