using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

public sealed class GraphBatchThrottleClassifierTests
{
    private static readonly RequestCost s_batch = new(
        3,
        SharePointResourceUnitEstimator.BatchCategory
    );

    [Test]
    public async Task ClassifyAsync_Batch_CountsEachThrottledInnerResponseAndTakesTheLongestRetryAfter()
    {
        using var response = Ok(
            """
            {"responses":[
              {"id":"1","status":429,"headers":{"Retry-After":"20"}},
              {"id":"2","status":503,"headers":{"retry-after":"5"}},
              {"id":"3","status":503},
              {"id":"4","status":200}
            ]}
            """
        );

        var signal = await Classify(response, s_batch);

        signal.ShouldBe(new ThrottleSignal(503, TimeSpan.FromSeconds(20), 2));
    }

    [Test]
    public async Task ClassifyAsync_InnerRetryAfterDate_IsTheTimeUntilTheDate()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var response = Ok(
            """{"responses":[{"id":"1","status":429,"headers":{"Retry-After":"Thu, 01 Jan 2026 00:00:30 GMT"}}]}"""
        );

        var signal = await new GraphBatchThrottleClassifier(time).ClassifyAsync(
            response,
            s_batch,
            CancellationToken.None
        );

        signal.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task ClassifyAsync_BatchWithoutThrottles_IsTheOuterStatus()
    {
        using var response = Ok("""{"responses":[{"id":"1","status":204}]}""");

        var signal = await Classify(response, s_batch);

        signal.ShouldBe(new ThrottleSignal(200));
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"value":[]}""")]
    [Arguments("""{"responses":{}}""")]
    public async Task ClassifyAsync_BadBatchBody_IsTheOuterStatus(string body)
    {
        using var response = Ok(body);

        var signal = await Classify(response, s_batch);

        signal.ShouldBe(new ThrottleSignal(200));
    }

    [Test]
    public async Task ClassifyAsync_Batch_KeepsTheBodyReadable()
    {
        const string Body = """{"responses":[{"id":"1","status":429}]}""";
        using var response = Ok(Body);

        await Classify(response, s_batch);

        (await response.Content.ReadAsStringAsync()).ShouldBe(Body);
    }

    [Test]
    public async Task ClassifyAsync_NotABatch_DoesNotReadTheBody()
    {
        using var body = new UnreadableContent();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };

        var signal = await Classify(
            response,
            new RequestCost(1, SharePointResourceUnitEstimator.SharePointCategory)
        );

        body.WasRead.ShouldBeFalse();
        signal.IsThrottled.ShouldBeFalse();
    }

    [Test]
    public async Task ClassifyAsync_ThrottledBatch_UsesTheOuterResponse()
    {
        using var body = new UnreadableContent();
        using var response = TestHelpers.Throttled();
        response.Content = body;

        var signal = await Classify(response, s_batch);

        body.WasRead.ShouldBeFalse();
        signal.ShouldBe(new ThrottleSignal(429, TimeSpan.Zero, 1));
    }

    private static ValueTask<ThrottleSignal> Classify(
        HttpResponseMessage response,
        RequestCost cost
    ) => new GraphBatchThrottleClassifier().ClassifyAsync(response, cost, CancellationToken.None);

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

internal sealed class UnreadableContent : HttpContent
{
    public bool WasRead { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        WasRead = true;
        return Task.CompletedTask;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
