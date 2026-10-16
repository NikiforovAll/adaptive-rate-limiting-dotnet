using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nall.RateLimiting.Adaptive;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers named <see cref="AdaptiveRateLimiter"/> instances.
/// </summary>
/// <remarks>
/// Each limiter is a keyed singleton: resolve it with
/// <c>GetRequiredKeyedService&lt;AdaptiveRateLimiter&gt;(name)</c>. A change of its named
/// <see cref="AdaptiveRateLimiterOptions"/> is applied with
/// <see cref="AdaptiveRateLimiter.UpdateOptions"/>; settings that are not valid are logged and
/// ignored.
/// </remarks>
public static partial class AdaptiveRateLimiterServiceCollectionExtensions
{
    /// <summary>
    /// The configuration section that holds one subsection of settings for each limiter name.
    /// </summary>
    public const string LimitersSection = "AdaptiveRateLimiting:Limiters";

    /// <summary>
    /// Adds the limiter <paramref name="name"/>. Its settings bind from the
    /// <c>AdaptiveRateLimiting:Limiters:&lt;name&gt;</c> section of the
    /// <see cref="IConfiguration"/> in the container, if there is one, and reload with it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the limiter.</param>
    /// <returns>A builder for the services of the limiter.</returns>
    public static AdaptiveRateLimiterBuilder AddAdaptiveRateLimiter(
        this IServiceCollection services,
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return AddLimiter(
            services,
            name,
            sp => sp.GetService<IConfiguration>()?.GetSection(LimitersSection).GetSection(name)
        );
    }

    /// <summary>
    /// Adds the limiter <paramref name="name"/>. Its settings bind as in
    /// <see cref="AddAdaptiveRateLimiter(IServiceCollection, string)"/>, and then
    /// <paramref name="configure"/> runs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the limiter.</param>
    /// <param name="configure">Changes the settings after the binding.</param>
    /// <returns>A builder for the services of the limiter.</returns>
    public static AdaptiveRateLimiterBuilder AddAdaptiveRateLimiter(
        this IServiceCollection services,
        string name,
        Action<AdaptiveRateLimiterOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = services.AddAdaptiveRateLimiter(name);
        services.Configure(name, configure);
        return builder;
    }

    /// <summary>
    /// Adds the limiter <paramref name="name"/> with settings bound from
    /// <paramref name="configuration"/>. The settings reload when the configuration changes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the limiter.</param>
    /// <param name="configuration">The section that holds the settings.</param>
    /// <returns>A builder for the services of the limiter.</returns>
    public static AdaptiveRateLimiterBuilder AddAdaptiveRateLimiter(
        this IServiceCollection services,
        string name,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);

        return AddLimiter(services, name, _ => configuration);
    }

    /// <summary>
    /// Sends the requests of this client through the limiter <paramref name="name"/>, with the
    /// cost estimator of the limiter.
    /// </summary>
    /// <remarks>
    /// Add a retry handler before this one, so that each retry waits for units too.
    /// </remarks>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="name">
    /// The name of a limiter added with <c>AddAdaptiveRateLimiter</c>.
    /// </param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddAdaptiveRateLimitHandler(
        this IHttpClientBuilder builder,
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.AddHttpMessageHandler(sp => CreateHandler(sp, name));
    }

    private static AdaptiveRateLimitHandler CreateHandler(IServiceProvider services, string name)
    {
        var limiter = services.GetRequiredKeyedService<AdaptiveRateLimiter>(name);
        var registration = services
            .GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterRegistration>>()
            .Get(name);
        return new AdaptiveRateLimitHandler(limiter, registration.CostEstimator?.Invoke(services));
    }

    private static AdaptiveRateLimiterBuilder AddLimiter(
        IServiceCollection services,
        string name,
        Func<IServiceProvider, IConfiguration?> section
    )
    {
        services.AddOptions();
        services.AddSingleton<IConfigureOptions<AdaptiveRateLimiterOptions>>(sp => new BindSection(
            name,
            section(sp),
            sp.GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterRegistration>>()
        ));
        services.AddSingleton<IOptionsChangeTokenSource<AdaptiveRateLimiterOptions>>(
            sp => new SectionChangeTokenSource(name, section(sp))
        );
        services.TryAddKeyedSingleton(name, static (sp, key) => CreateLimiter(sp, (string)key!));
        return new AdaptiveRateLimiterBuilder(services, name);
    }

    private static AdaptiveRateLimiter CreateLimiter(IServiceProvider services, string name)
    {
        var monitor = services.GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterOptions>>();
        var registration = services
            .GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterRegistration>>()
            .Get(name);
        var logger = services
            .GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(AdaptiveRateLimiter).FullName!);
        var limiter = new AdaptiveRateLimiter(
            name,
            monitor.Get(name),
            registration.Controller?.Invoke(services),
            registration.ThrottleClassifier?.Invoke(services),
            services.GetService<TimeProvider>(),
            services.GetService<IMeterFactory>(),
            logger
        );

        try
        {
            if (registration.InstanceCount is { } source)
            {
                limiter.UseInstanceCount(source(services));
            }

            var subscription = monitor.OnChange(
                (options, changed) =>
                {
                    if (changed != name)
                    {
                        return;
                    }

                    try
                    {
                        limiter.UpdateOptions(options);
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        if (logger is not null)
                        {
                            LogInvalidOptions(logger, ex, name);
                        }
                    }
                }
            );
            if (subscription is not null)
            {
                limiter.Own(subscription);
            }
        }
        catch
        {
            limiter.Dispose();
            throw;
        }

        return limiter;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The new settings of rate limiter '{Name}' are not valid; it keeps the old ones."
    )]
    private static partial void LogInvalidOptions(ILogger logger, Exception exception, string name);

    private sealed class BindSection(
        string name,
        IConfiguration? section,
        IOptionsMonitor<AdaptiveRateLimiterRegistration> registrations
    ) : IConfigureNamedOptions<AdaptiveRateLimiterOptions>
    {
        public void Configure(string? optionsName, AdaptiveRateLimiterOptions options)
        {
            if (optionsName != name)
            {
                return;
            }

            registrations.Get(name).Defaults?.Invoke(options);
            if (section is not null)
            {
                AdaptiveRateLimiterOptionsBinder.Bind(section, options);
            }
        }

        public void Configure(AdaptiveRateLimiterOptions options) =>
            Configure(Options.Options.DefaultName, options);
    }

    private sealed class SectionChangeTokenSource(string name, IConfiguration? section)
        : IOptionsChangeTokenSource<AdaptiveRateLimiterOptions>
    {
        public string Name => name;

        public IChangeToken GetChangeToken() =>
            section?.GetReloadToken() ?? new CancellationChangeToken(CancellationToken.None);
    }
}
