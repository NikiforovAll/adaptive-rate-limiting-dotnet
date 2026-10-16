using System.Net;
using System.Net.Http.Headers;

namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// The default classifier: a response is a throttle if it is 429, or 503 with
/// <c>Retry-After</c>. It reads only the status and the headers.
/// </summary>
public sealed class HttpThrottleClassifier : IThrottleClassifier
{
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates the classifier.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock for a <c>Retry-After</c> date. <see cref="TimeProvider.System"/> if null.
    /// </param>
    public HttpThrottleClassifier(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true for 429, and for 503 with a <c>Retry-After</c>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> of the response, if any.</param>
    /// <returns>True if the status is a throttle.</returns>
    public static bool IsThrottled(int statusCode, TimeSpan? retryAfter) =>
        statusCode == (int)HttpStatusCode.TooManyRequests
        || (statusCode == (int)HttpStatusCode.ServiceUnavailable && retryAfter is not null);

    /// <summary>
    /// Classifies the response from its status and headers.
    /// </summary>
    /// <param name="response">The response to the request.</param>
    /// <returns>The throttle signal of the response.</returns>
    public ThrottleSignal Classify(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;
        var retryAfter = RetryAfterOf(response.Headers.RetryAfter);
        return new ThrottleSignal(status, retryAfter, IsThrottled(status, retryAfter) ? 1 : 0);
    }

    /// <inheritdoc />
    public ValueTask<ThrottleSignal> ClassifyAsync(
        HttpResponseMessage response,
        RequestCost cost,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(Classify(response));

    private TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header) =>
        header switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - _time.GetUtcNow(),
            _ => null,
        };
}
