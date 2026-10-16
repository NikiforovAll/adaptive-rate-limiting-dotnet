namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Sends each request through an <see cref="AdaptiveRateLimiter"/>: waits for the units of the
/// request, sends it, and gives the response to the limiter.
/// </summary>
/// <remarks>
/// Put the handler inside a retry handler, so that each retry waits for units too.
/// </remarks>
public sealed class AdaptiveRateLimitHandler : DelegatingHandler
{
    private readonly AdaptiveRateLimiter _limiter;
    private readonly IRequestCostEstimator _costEstimator;

    /// <summary>
    /// Creates the handler.
    /// </summary>
    /// <param name="limiter">The limiter. The handler does not dispose it.</param>
    /// <param name="costEstimator">
    /// Tells the cost of each request. If null, each request costs one unit.
    /// </param>
    public AdaptiveRateLimitHandler(
        AdaptiveRateLimiter limiter,
        IRequestCostEstimator? costEstimator = null
    )
    {
        ArgumentNullException.ThrowIfNull(limiter);

        _limiter = limiter;
        _costEstimator = costEstimator ?? new FixedCostEstimator(1);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var cost = await _costEstimator
            .EstimateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var sentAt = await _limiter.WaitAsync(cost, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            await _limiter
                .OnResponseAsync(response, cost, sentAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
