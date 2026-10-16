using System.Diagnostics.CodeAnalysis;
using Nall.RateLimiting.Adaptive;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Configures the services of one named <see cref="AdaptiveRateLimiter"/>.
/// </summary>
public sealed class AdaptiveRateLimiterBuilder
{
    internal AdaptiveRateLimiterBuilder(IServiceCollection services, string name)
    {
        Services = services;
        Name = name;
    }

    /// <summary>
    /// The name of the limiter.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Uses <typeparamref name="T"/> to tell the cost of each request that the limit handler
    /// sends. <typeparamref name="T"/> is resolved from the container, or created with its
    /// services.
    /// </summary>
    /// <typeparam name="T">The cost estimator.</typeparam>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithCostEstimator<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >()
        where T : class, IRequestCostEstimator => Configure(r => r.CostEstimator = Create<T>);

    /// <summary>
    /// Uses <paramref name="estimator"/> to tell the cost of each request that the limit handler
    /// sends.
    /// </summary>
    /// <param name="estimator">The cost estimator.</param>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithCostEstimator(IRequestCostEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(estimator);

        return Configure(r => r.CostEstimator = _ => estimator);
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to tell if a response is a throttle.
    /// </summary>
    /// <typeparam name="T">The throttle classifier.</typeparam>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithThrottleClassifier<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >()
        where T : class, IThrottleClassifier => Configure(r => r.ThrottleClassifier = Create<T>);

    /// <summary>
    /// Uses <paramref name="classifier"/> to tell if a response is a throttle.
    /// </summary>
    /// <param name="classifier">The throttle classifier.</param>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithThrottleClassifier(IThrottleClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        return Configure(r => r.ThrottleClassifier = _ => classifier);
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to change the rate, in place of the built-in controller
    /// that <see cref="AdaptiveRateLimiterOptions.Controller"/> selects.
    /// </summary>
    /// <typeparam name="T">The rate controller.</typeparam>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithController<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >()
        where T : class, IRateController => Configure(r => r.Controller = Create<T>);

    /// <summary>
    /// Uses <paramref name="controller"/> to change the rate, in place of the built-in
    /// controller that <see cref="AdaptiveRateLimiterOptions.Controller"/> selects.
    /// </summary>
    /// <param name="controller">The rate controller.</param>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithController(IRateController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        return Configure(r => r.Controller = _ => controller);
    }

    /// <summary>
    /// Takes the instance count from <typeparamref name="T"/>, in place of
    /// <see cref="AdaptiveRateLimiterOptions.InstanceCount"/>.
    /// </summary>
    /// <typeparam name="T">The instance count source.</typeparam>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithInstanceCount<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >()
        where T : class, IInstanceCountSource => Configure(r => r.InstanceCount = Create<T>);

    /// <summary>
    /// Takes the instance count from <paramref name="source"/>, in place of
    /// <see cref="AdaptiveRateLimiterOptions.InstanceCount"/>.
    /// </summary>
    /// <param name="source">The instance count source.</param>
    /// <returns>This builder.</returns>
    public AdaptiveRateLimiterBuilder WithInstanceCount(IInstanceCountSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Configure(r => r.InstanceCount = _ => source);
    }

    private static T Create<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >(IServiceProvider services)
        where T : class => ActivatorUtilities.GetServiceOrCreateInstance<T>(services);

    private AdaptiveRateLimiterBuilder Configure(Action<AdaptiveRateLimiterRegistration> configure)
    {
        Services.Configure(Name, configure);
        return this;
    }
}
