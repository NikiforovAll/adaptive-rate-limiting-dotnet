using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Sends SharePoint CSOM calls (<c>/_vti_bin/client.svc/ProcessQuery</c>) through the same
/// limiter as Graph, so that both share the resource unit budget of the app.
/// </summary>
/// <remarks>
/// A CSOM call can hold many operations, and its body does not tell its real cost, so each call
/// has a fixed cost.
/// </remarks>
public static class SharePointCsom
{
    /// <summary>
    /// The default units of one CSOM call: 2.
    /// </summary>
    public const int DefaultCallUnits = 2;

    /// <summary>
    /// The category of a CSOM call.
    /// </summary>
    public const string Category = "csom";

    /// <summary>
    /// The most retries that the Kiota retry handler allows.
    /// </summary>
    internal const int MaxRetry = 10;

    /// <summary>
    /// Creates the handler pipeline for CSOM: the Kiota retry handler, then the limit handler,
    /// then <paramref name="innerHandler"/>. Each retry then pays its units.
    /// </summary>
    /// <remarks>
    /// Give the pipeline to the HttpClient of the CSOM client in place of its own retry
    /// handler. The HttpClient disposes the pipeline, but not <paramref name="limiter"/>.
    /// </remarks>
    /// <param name="limiter">The limiter that Graph also uses.</param>
    /// <param name="innerHandler">
    /// The handler that sends the call. A new <see cref="SocketsHttpHandler"/> without cookies
    /// if null.
    /// </param>
    /// <param name="callUnits">The units of one call. Zero or more.</param>
    /// <returns>The outer handler of the pipeline.</returns>
    public static DelegatingHandler CreateHandler(
        AdaptiveRateLimiter limiter,
        HttpMessageHandler? innerHandler = null,
        int callUnits = DefaultCallUnits
    )
    {
        ArgumentNullException.ThrowIfNull(limiter);

        var estimator = new FixedCostEstimator(callUnits, Category);
        return new RetryHandler(new RetryHandlerOption { MaxRetry = MaxRetry })
        {
            InnerHandler = new AdaptiveRateLimitHandler(limiter, estimator)
            {
                InnerHandler =
                    innerHandler
                    ?? new SocketsHttpHandler
                    {
                        UseCookies = false,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    },
            },
        };
    }
}
