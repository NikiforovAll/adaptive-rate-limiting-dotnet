using System.Net;
using System.Text;
using Microsoft.Graph;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

public sealed class GraphClientHandlersTests
{
    [Test]
    public void CreateDefaultHandlers_PutsTheLimitHandlerRightAfterTheRetryHandler()
    {
        using var limiter = SharePointRateLimiter.Create(TestHelpers.NewName());

        var handlers = GraphClientHandlers.CreateDefaultHandlers(limiter);

        var retry = handlers.IndexOf(handlers.OfType<RetryHandler>().Single());
        handlers[retry + 1].ShouldBeOfType<AdaptiveRateLimitHandler>();
        handlers.OfType<AdaptiveRateLimitHandler>().ShouldHaveSingleItem();
    }

    [Test]
    public void InsertRateLimitHandler_NoRetryHandler_AddsItAtTheEnd()
    {
        using var limiter = SharePointRateLimiter.Create(TestHelpers.NewName());
        using var first = new RedirectHandler();
        using var limit = new AdaptiveRateLimitHandler(limiter);
        var handlers = new List<DelegatingHandler> { first };

        GraphClientHandlers.InsertRateLimitHandler(handlers, limit);

        handlers.ShouldBe([first, limit]);
    }

    [Test]
    public async Task GraphClient_ThrottledThenOk_PaysForEachRetryAndCutsTheRate()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = 0L;
        using var listener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var stub = new StubHandler(
            TestHelpers.Throttled,
            () => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = CreateClient(limiter, stub);

        using var response = await client.GetAsync(
            new Uri("https://graph.microsoft.com/v1.0/drives/d/items/i/children")
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.Calls.ShouldBe(2);
        units.ShouldBe(4);
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task GraphClient_NotSharePoint_SkipsTheLimiterAndRecordsTheThrottle()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = 0L;
        using var listener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        var throttled = new List<Dictionary<string, object?>>();
        using var throttledListener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.ThrottledInstrument,
            (_, tags) => throttled.Add(tags)
        );
        using var stub = new StubHandler(
            TestHelpers.Throttled,
            () => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = CreateClient(limiter, stub);

        using var response = await client.GetAsync(
            new Uri("https://graph.microsoft.com/v1.0/users/u")
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        units.ShouldBe(0);
        limiter.UnitsPerMinute.ShouldBe(600);
        throttled
            .ShouldHaveSingleItem()[AdaptiveRateLimiterMetrics.CategoryTag]
            .ShouldBe(SharePointResourceUnitEstimator.GraphCategory);
    }

    [Test]
    public async Task GraphClient_Batch_PaysTheInnerUnitsAndRecordsEachThrottledInnerResponse()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = 0L;
        using var unitsListener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        var throttled = new List<(long Count, Dictionary<string, object?> Tags)>();
        using var throttledListener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.ThrottledInstrument,
            (value, tags) => throttled.Add((value, tags))
        );
        const string ResponseBody = """
            {"responses":[
              {"id":"1","status":429,"headers":{"Retry-After":"20"}},
              {"id":"2","status":429},
              {"id":"3","status":200}
            ]}
            """;
        using var stub = new StubHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseBody) }
        );
        using var client = CreateClient(limiter, stub);
        using var content = new StringContent(
            """
            {"requests":[
              {"id":"1","method":"GET","url":"/drives/d/items/i/permissions"},
              {"id":"2","method":"GET","url":"/drives/d/items/i/children"},
              {"id":"3","method":"GET","url":"/drives/d/items/i"}
            ]}
            """,
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync(
            new Uri("https://graph.microsoft.com/v1.0/$batch"),
            content
        );

        units.ShouldBe(8);
        var (count, tags) = throttled.ShouldHaveSingleItem();
        count.ShouldBe(2);
        tags[AdaptiveRateLimiterMetrics.RetryAfterTag].ShouldBe("le_30s");
        tags[AdaptiveRateLimiterMetrics.CategoryTag]
            .ShouldBe(SharePointResourceUnitEstimator.BatchCategory);
        limiter.UnitsPerMinute.ShouldBe(420);
        (await response.Content.ReadAsStringAsync()).ShouldBe(ResponseBody);
    }

    [Test]
    public async Task GraphClient_DisabledLimiter_DoesNotReadTheBatchResponse()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { Enabled = false }
        );
        var throttled = 0L;
        using var listener = TestHelpers.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.ThrottledInstrument,
            v => throttled += v
        );
        using var body = new UnreadableContent();
        using var stub = new StubHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = body }
        );
        using var client = CreateClient(limiter, stub);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://graph.microsoft.com/v1.0/$batch")
        )
        {
            Content = new StringContent(
                """{"requests":[{"id":"1","method":"POST","url":"/drives/d/items/i/permanentDelete"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );

        body.WasRead.ShouldBeFalse();
        throttled.ShouldBe(0);
    }

    private static HttpClient CreateClient(AdaptiveRateLimiter limiter, HttpMessageHandler stub) =>
        GraphClientFactory.Create(
            GraphClientHandlers.CreateDefaultHandlers(limiter),
            finalHandler: stub,
            disposeHandler: false
        );
}
