using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

public sealed class SharePointRateLimiterBuilderExtensionsTests
{
    [Test]
    public async Task ForSharePoint_HttpClient_PaysTheUnitsAndReadsTheBatchThrottles()
    {
        var name = TestHelpers.NewName();
        var services = new ServiceCollection();
        services
            .AddAdaptiveRateLimiter(name, o => o.InitialUnitsPerMinute = 600)
            .ForSharePoint(new SharePointResourceUnitCosts { Permissions = 9 });
        services
            .AddHttpClient("graph")
            .AddAdaptiveRateLimitHandler(name)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new StubHandler(() =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"responses":[{"id":"1","status":429}]}"""),
                    }
                )
            );
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name);
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
        using var content = new StringContent(
            """{"requests":[{"id":"1","method":"GET","url":"/drives/d/items/i/permissions"}]}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync(
            new Uri("https://graph.microsoft.com/v1.0/$batch"),
            content
        );

        units.ShouldBe(9);
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public void ForSharePoint_UsesTheSharePointDefaults()
    {
        var name = TestHelpers.NewName();
        var services = new ServiceCollection();
        services.AddAdaptiveRateLimiter(name).ForSharePoint();
        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterOptions>>()
            .Get(name);

        (
            options.InitialUnitsPerMinute,
            options.MinUnitsPerMinute,
            options.MaxUnitsPerMinute
        ).ShouldBe((1500, 200, 5625));
        provider.GetRequiredKeyedService<AdaptiveRateLimiter>(name).UnitsPerMinute.ShouldBe(1500);
    }

    [Test]
    public void ForSharePoint_ConfigurationOverridesTheDefaults()
    {
        var name = TestHelpers.NewName();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"AdaptiveRateLimiting:Limiters:{name}:MaxUnitsPerMinute"] = "2250",
                    }
                )
                .Build()
        );
        services.AddAdaptiveRateLimiter(name).ForSharePoint();
        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<AdaptiveRateLimiterOptions>>()
            .Get(name);

        (options.InitialUnitsPerMinute, options.MaxUnitsPerMinute).ShouldBe((1500, 2250));
    }

    [Test]
    public void Create_WithoutOptions_UsesTheSharePointDefaults()
    {
        using var limiter = SharePointRateLimiter.Create(TestHelpers.NewName());

        limiter.UnitsPerMinute.ShouldBe(1500);
    }
}
