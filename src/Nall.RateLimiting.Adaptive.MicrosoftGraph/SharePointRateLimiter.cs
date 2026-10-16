using System.Diagnostics.Metrics;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Creates a limiter for the SharePoint resource unit budget of an app.
/// </summary>
public static class SharePointRateLimiter
{
    /// <summary>
    /// Creates a limiter that reads throttled inner <c>$batch</c> responses with a
    /// <see cref="GraphBatchThrottleClassifier"/>.
    /// </summary>
    /// <remarks>
    /// The default maximum of <see cref="AdaptiveRateLimiterOptions"/> is 5,625 units per minute:
    /// 90% of the per-app budget of the largest tenants. Set
    /// <see cref="AdaptiveRateLimiterOptions.MaxUnitsPerMinute"/> to the budget of your tenant.
    /// </remarks>
    /// <param name="name">The name of the limiter, for metrics.</param>
    /// <param name="options">The options. The defaults if null.</param>
    /// <param name="timeProvider">The clock. <see cref="TimeProvider.System"/> if null.</param>
    /// <param name="meterFactory">Creates the meter of the metrics. A static meter if null.</param>
    /// <returns>The limiter.</returns>
    public static AdaptiveRateLimiter Create(
        string name,
        AdaptiveRateLimiterOptions? options = null,
        TimeProvider? timeProvider = null,
        IMeterFactory? meterFactory = null
    ) =>
        new(
            name,
            options ?? new AdaptiveRateLimiterOptions(),
            controller: null,
            new GraphBatchThrottleClassifier(timeProvider),
            timeProvider,
            meterFactory
        );
}
