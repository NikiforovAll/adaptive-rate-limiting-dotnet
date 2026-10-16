using Nall.RateLimiting.Adaptive.MicrosoftGraph;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Configures a named limiter for the SharePoint resource unit budget.
/// </summary>
public static class SharePointRateLimiterBuilderExtensions
{
    /// <summary>
    /// Counts the resource units of each request with a
    /// <see cref="SharePointResourceUnitEstimator"/>, reads throttled inner <c>$batch</c>
    /// responses with a <see cref="GraphBatchThrottleClassifier"/>, and uses the default rates of
    /// <see cref="SharePointRateLimiter.CreateOptions"/>. The configuration can change the rates.
    /// </summary>
    /// <remarks>
    /// The estimator applies to an HttpClient that has
    /// <c>AddAdaptiveRateLimitHandler(name)</c>. For a Graph client, pass the limiter to
    /// <see cref="GraphClientHandlers.CreateDefaultHandlers"/>.
    /// </remarks>
    /// <param name="builder">The limiter builder.</param>
    /// <param name="costs">The units of each kind of request. The defaults if null.</param>
    /// <returns><paramref name="builder"/>.</returns>
    public static AdaptiveRateLimiterBuilder ForSharePoint(
        this AdaptiveRateLimiterBuilder builder,
        SharePointResourceUnitCosts? costs = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithDefaults(SharePointRateLimiter.ApplyDefaults)
            .WithCostEstimator(new SharePointResourceUnitEstimator(costs))
            .WithThrottleClassifier<GraphBatchThrottleClassifier>();
    }
}
