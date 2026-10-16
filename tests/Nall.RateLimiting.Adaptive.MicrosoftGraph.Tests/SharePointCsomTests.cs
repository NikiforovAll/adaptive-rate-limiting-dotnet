using System.Net;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

public sealed class SharePointCsomTests
{
    private static readonly Uri s_processQuery = new(
        "https://contoso.sharepoint.com/sites/s/_vti_bin/client.svc/ProcessQuery"
    );

    [Test]
    public async Task Send_ThrottledThenOk_RetriesThroughTheLimiterAndCutsTheRate()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = new List<(long Units, Dictionary<string, object?> Tags)>();
        using var unitsListener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            (value, tags) => units.Add((value, tags))
        );
        var throttled = new List<Dictionary<string, object?>>();
        using var throttledListener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.ThrottledInstrument,
            (_, tags) => throttled.Add(tags)
        );
        var stub = new StubHandler(
            TestHelpers.Throttled,
            () => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(SharePointCsom.CreateHandler(limiter, stub));

        using var response = await client.PostAsync(
            s_processQuery,
            new StringContent("<Request/>")
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.Bodies.ShouldBe(["<Request/>", "<Request/>"]);
        units.Sum(u => u.Units).ShouldBe(4);
        units.ShouldAllBe(u =>
            (string?)u.Tags[AdaptiveRateLimiterMetrics.CategoryTag] == SharePointCsom.Category
        );
        throttled
            .ShouldHaveSingleItem()[AdaptiveRateLimiterMetrics.CategoryTag]
            .ShouldBe(SharePointCsom.Category);
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task Send_CallUnits_ChargesEachCall()
    {
        using var limiter = SharePointRateLimiter.Create(TestHelpers.NewName());
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        var stub = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(
            SharePointCsom.CreateHandler(limiter, stub, callUnits: 7)
        );

        using var first = await client.PostAsync(s_processQuery, new StringContent("a"));
        using var second = await client.PostAsync(s_processQuery, new StringContent("b"));

        units.ShouldBe(14);
    }

    [Test]
    public async Task Send_AlwaysThrottled_ThrowsAfterTheMostRetries()
    {
        using var limiter = SharePointRateLimiter.Create(TestHelpers.NewName());
        var stub = new StubHandler(TestHelpers.Throttled);
        using var client = new HttpClient(SharePointCsom.CreateHandler(limiter, stub));

        await Should.ThrowAsync<AggregateException>(
            client.PostAsync(s_processQuery, new StringContent("a"))
        );

        stub.Calls.ShouldBe(SharePointCsom.MaxRetry + 1);
    }

    [Test]
    public async Task Send_QueueFull_ThrowsRateLimitRejectedException()
    {
        using var limiter = SharePointRateLimiter.Create(
            TestHelpers.NewName(),
            new() { InitialUnitsPerMinute = 600, QueueLimit = 0 }
        );
        var stub = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(
            SharePointCsom.CreateHandler(limiter, stub, callUnits: 100)
        );
        using var first = await client.PostAsync(s_processQuery, new StringContent("a"));

        await Should.ThrowAsync<RateLimitRejectedException>(
            client.PostAsync(s_processQuery, new StringContent("b"))
        );
    }
}
