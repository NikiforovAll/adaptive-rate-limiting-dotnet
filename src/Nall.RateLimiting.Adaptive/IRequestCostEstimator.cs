namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Tells the cost of a request before it is sent.
/// </summary>
public interface IRequestCostEstimator
{
    /// <summary>
    /// Returns the cost of <paramref name="request"/>.
    /// </summary>
    /// <remarks>
    /// An estimator that reads the request body must leave the body readable, because a retry
    /// handler can send the same request again.
    /// </remarks>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Cancels the estimate.</param>
    /// <returns>The cost of the request.</returns>
    ValueTask<RequestCost> EstimateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    );
}
