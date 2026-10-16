using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Test]
    public void AddAdaptiveRateLimiter_BindsTheNamedSection()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var configuration = Configuration(
            ($"AdaptiveRateLimiting:Limiters:{name}:InitialUnitsPerMinute", "600"),
            ($"AdaptiveRateLimiting:Limiters:{name}:InstanceCount", "2"),
            ($"AdaptiveRateLimiting:Limiters:{name}:Controller", "Cubic"),
            ($"AdaptiveRateLimiting:Limiters:{name}:Cubic:Beta", "0.5")
        );
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddAdaptiveRateLimiter(name);
        using var provider = services.BuildServiceProvider();

        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);
        limiter.OnThrottled();

        limiter.InstanceCount.ShouldBe(2);
        limiter.UnitsPerMinute.ShouldBe(150);
    }

    [Test]
    public void AddAdaptiveRateLimiter_NoConfiguration_UsesTheDelegate()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var services = new ServiceCollection();
        services.AddAdaptiveRateLimiter(name, o => o.InitialUnitsPerMinute = 900);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name).UnitsPerMinute.ShouldBe(900);
    }

    [Test]
    public void AddAdaptiveRateLimiter_TwoNames_AreTwoLimiters()
    {
        var services = new ServiceCollection();
        services.AddAdaptiveRateLimiter("a", o => o.InitialUnitsPerMinute = 600);
        services.AddAdaptiveRateLimiter("b", o => o.InitialUnitsPerMinute = 900);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<AdaptiveRateLimiter>("a").UnitsPerMinute.ShouldBe(600);
        provider.GetRequiredKeyedService<AdaptiveRateLimiter>("b").UnitsPerMinute.ShouldBe(900);
    }

    [Test]
    public void AddAdaptiveRateLimiter_Section_ReloadsTheSettings()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var configuration = Configuration(("InitialUnitsPerMinute", "1200"));
        var services = new ServiceCollection();
        services.AddAdaptiveRateLimiter(name, configuration);
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);

        configuration["MaxUnitsPerMinute"] = "600";
        configuration["InitialUnitsPerMinute"] = "600";
        configuration.Reload();

        limiter.UnitsPerMinute.ShouldBe(600);
    }

    [Test]
    public void AddAdaptiveRateLimiter_InvalidReload_KeepsTheSettings()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var configuration = Configuration(("InitialUnitsPerMinute", "1200"));
        var services = new ServiceCollection().AddLogging();
        services.AddAdaptiveRateLimiter(name, configuration);
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);

        configuration["MinUnitsPerMinute"] = "0";
        configuration.Reload();

        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(840);
    }

    [Test]
    public void AddAdaptiveRateLimiter_UsesTheBuilderServices()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var services = new ServiceCollection();
        services
            .AddAdaptiveRateLimiter(name, o => o.InitialUnitsPerMinute = 1200)
            .WithController<HalfController>()
            .WithInstanceCount(new StaticInstanceCountSource(3));
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);

        limiter.InstanceCount.ShouldBe(3);
        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(200);
    }

    [Test]
    public async Task AddAdaptiveRateLimiter_MeterFactory_RecordsOnTheMeterOfTheContainer()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var services = new ServiceCollection().AddMetrics();
        services.AddAdaptiveRateLimiter(name);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var units = 0L;
        using var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (
                    i.Meter.Scope == factory
                    && i.Name == AdaptiveRateLimiterMetrics.UnitsInstrument
                )
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => units += v);
        listener.Start();

        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);
        await limiter.WaitAsync(new RequestCost(4), CancellationToken.None);

        units.ShouldBe(4);
    }

    [Test]
    public async Task AddAdaptiveRateLimitHandler_SendsThroughTheLimiter()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var services = new ServiceCollection();
        services
            .AddAdaptiveRateLimiter(name, o => o.InitialUnitsPerMinute = 600)
            .WithCostEstimator(new FixedCostEstimator(5));
        services
            .AddHttpClient("api")
            .AddAdaptiveRateLimitHandler(name)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new StatusHandler(HttpStatusCode.TooManyRequests)
            );
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var response = await client.GetAsync(new Uri("https://api.example.com/items"));

        units.ShouldBe(5);
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task Dispose_TheContainer_DisposesTheLimiter()
    {
        var name = AdaptiveRateLimiterTests.NewName();
        var services = new ServiceCollection();
        services.AddAdaptiveRateLimiter(name);
        var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);

        await provider.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            limiter.WaitAsync(new RequestCost(1), CancellationToken.None).AsTask()
        );
    }

    private static IConfigurationRoot Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value)))
            .Build();

    private sealed class HalfController : IRateController
    {
        public double OnThrottled(in RateControllerState state) => state.Rate / 2;

        public double? OnSuccess(in RateControllerState state) => null;
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}
