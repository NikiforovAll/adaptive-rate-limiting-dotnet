namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Gives each request the same cost.
/// </summary>
public sealed class FixedCostEstimator : IRequestCostEstimator
{
    private readonly RequestCost _cost;

    /// <summary>
    /// Creates an estimator that gives each request <paramref name="units"/>.
    /// </summary>
    /// <param name="units">The units of each request. Zero or more.</param>
    /// <param name="category">The category of each request, for metrics.</param>
    public FixedCostEstimator(int units, string? category = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);

        _cost = new RequestCost(units, category);
    }

    /// <summary>
    /// The cost that each request gets.
    /// </summary>
    public RequestCost Cost => _cost;

    /// <inheritdoc />
    public ValueTask<RequestCost> EstimateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(_cost);
}
