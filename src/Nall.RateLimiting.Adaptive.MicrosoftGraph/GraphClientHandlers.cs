using Microsoft.Graph;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Puts the limit handler in the Graph handler pipeline, inside the Kiota retry handler, so that
/// each retry also waits for its units.
/// </summary>
public static class GraphClientHandlers
{
    /// <summary>
    /// Inserts <paramref name="rateLimitHandler"/> after the first <see cref="RetryHandler"/>, or
    /// at the end if there is none.
    /// </summary>
    /// <param name="handlers">The handlers, such as from <see cref="GraphClientFactory.CreateDefaultHandlers"/>.</param>
    /// <param name="rateLimitHandler">The limit handler.</param>
    /// <returns><paramref name="handlers"/>.</returns>
    public static IList<DelegatingHandler> InsertRateLimitHandler(
        IList<DelegatingHandler> handlers,
        DelegatingHandler rateLimitHandler
    )
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(rateLimitHandler);

        var retry = -1;
        for (var i = 0; i < handlers.Count; i++)
        {
            if (handlers[i] is RetryHandler)
            {
                retry = i;
                break;
            }
        }

        handlers.Insert(retry < 0 ? handlers.Count : retry + 1, rateLimitHandler);
        return handlers;
    }

    /// <summary>
    /// Creates the default Graph handlers with an <see cref="AdaptiveRateLimitHandler"/> after
    /// the retry handler. Pass them to <see cref="GraphClientFactory"/> to create the HttpClient
    /// of a Graph client.
    /// </summary>
    /// <remarks>
    /// Create <paramref name="limiter"/> with a <see cref="GraphBatchThrottleClassifier"/>, so
    /// that it also slows down on throttled inner <c>$batch</c> responses.
    /// </remarks>
    /// <param name="limiter">The limiter.</param>
    /// <param name="costEstimator">
    /// Tells the cost of each request. A <see cref="SharePointResourceUnitEstimator"/> with the
    /// default costs if null.
    /// </param>
    /// <param name="graphClientOptions">The options of the default Graph handlers.</param>
    /// <returns>The handlers.</returns>
    public static IList<DelegatingHandler> CreateDefaultHandlers(
        AdaptiveRateLimiter limiter,
        IRequestCostEstimator? costEstimator = null,
        GraphClientOptions? graphClientOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(limiter);

        return InsertRateLimitHandler(
            GraphClientFactory.CreateDefaultHandlers(graphClientOptions),
            new AdaptiveRateLimitHandler(
                limiter,
                costEstimator ?? new SharePointResourceUnitEstimator()
            )
        );
    }
}
