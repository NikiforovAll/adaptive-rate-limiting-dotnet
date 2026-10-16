namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Tells if a response is a throttle.
/// </summary>
public interface IThrottleClassifier
{
    /// <summary>
    /// Reads <paramref name="response"/> and returns what it says about the rate.
    /// </summary>
    /// <remarks>
    /// A classifier that reads the body must leave it readable for the caller.
    /// </remarks>
    /// <param name="response">The response to the request.</param>
    /// <param name="cost">The cost of the request, from the estimator.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The throttle signal of the response.</returns>
    ValueTask<ThrottleSignal> ClassifyAsync(
        HttpResponseMessage response,
        RequestCost cost,
        CancellationToken cancellationToken
    );
}
