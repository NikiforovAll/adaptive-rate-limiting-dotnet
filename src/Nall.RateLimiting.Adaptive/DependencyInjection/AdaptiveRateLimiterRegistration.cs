using Nall.RateLimiting.Adaptive;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The services of one named limiter, kept as named options.
/// </summary>
internal sealed class AdaptiveRateLimiterRegistration
{
    public Func<IServiceProvider, IRequestCostEstimator>? CostEstimator { get; set; }

    public Func<IServiceProvider, IThrottleClassifier>? ThrottleClassifier { get; set; }

    public Func<IServiceProvider, IRateController>? Controller { get; set; }

    public Func<IServiceProvider, IInstanceCountSource>? InstanceCount { get; set; }
}
