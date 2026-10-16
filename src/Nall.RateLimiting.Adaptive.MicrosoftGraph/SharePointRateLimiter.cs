using System.Diagnostics.Metrics;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Creates a limiter for the SharePoint resource unit budget of an app.
/// </summary>
public static class SharePointRateLimiter
{
    /// <summary>
    /// Creates settings with defaults for the SharePoint resource unit budget of an app.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SharePoint gives each app 1,250 to 6,250 resource units per minute in a tenant, by the
    /// license count of the tenant, and 1,200,000 to 6,000,000 in 24 hours
    /// (<see href="https://learn.microsoft.com/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online#application-throttling">Microsoft docs</see>).
    /// The defaults are:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="AdaptiveRateLimiterOptions.InitialUnitsPerMinute"/>: 1,500. Close to the
    /// budget of a small tenant, so that the first minutes cause few throttles.</item>
    /// <item><see cref="AdaptiveRateLimiterOptions.MinUnitsPerMinute"/>: 200. A
    /// <c>$batch</c> of 20 permission requests (100 units) waits 30 seconds at most, well within
    /// <see cref="AdaptiveRateLimiterOptions.MaxWait"/>.</item>
    /// <item><see cref="AdaptiveRateLimiterOptions.MaxUnitsPerMinute"/>: 5,625. 90% of the
    /// budget of the largest tenants. The rate grows to it only when the server accepts it.</item>
    /// </list>
    /// <para>
    /// The 24-hour budget is lower than the minute budget for a full day: at 5,625 units per
    /// minute, the largest budget runs out in about 18 hours. For a job that runs all day, set
    /// <see cref="AdaptiveRateLimiterOptions.MaxUnitsPerMinute"/> to the budget of your tenant.
    /// </para>
    /// </remarks>
    /// <returns>The settings.</returns>
    public static AdaptiveRateLimiterOptions CreateOptions()
    {
        var options = new AdaptiveRateLimiterOptions();
        ApplyDefaults(options);
        return options;
    }

    /// <summary>
    /// Creates a limiter that reads throttled inner <c>$batch</c> responses with a
    /// <see cref="GraphBatchThrottleClassifier"/>.
    /// </summary>
    /// <param name="name">The name of the limiter, for metrics.</param>
    /// <param name="options">The options. <see cref="CreateOptions"/> if null.</param>
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
            options ?? CreateOptions(),
            controller: null,
            new GraphBatchThrottleClassifier(timeProvider),
            timeProvider,
            meterFactory
        );

    internal static void ApplyDefaults(AdaptiveRateLimiterOptions options)
    {
        options.InitialUnitsPerMinute = 1500;
        options.MinUnitsPerMinute = 200;
        options.MaxUnitsPerMinute = 5625;
    }
}
