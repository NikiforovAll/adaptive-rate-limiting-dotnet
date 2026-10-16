using System.Net;

namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class AdaptiveRateLimitHandlerTests
{
    [Test]
    public async Task SendAsync_Throttled_PaysTheUnitsAndCutsTheRate()
    {
        using var limiter = new AdaptiveRateLimiter(
            AdaptiveRateLimiterTests.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var stub = new StubHandler(HttpStatusCode.TooManyRequests);
        using var client = new HttpClient(
            new AdaptiveRateLimitHandler(limiter, new FixedCostEstimator(3)) { InnerHandler = stub }
        );

        using var response = await client.GetAsync(new Uri("https://api.example.com/items"));

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        units.ShouldBe(3);
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task SendAsync_NoEstimator_ChargesOneUnit()
    {
        using var limiter = new AdaptiveRateLimiter(AdaptiveRateLimiterTests.NewName(), new());
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var stub = new StubHandler(HttpStatusCode.OK);
        using var client = new HttpClient(
            new AdaptiveRateLimitHandler(limiter) { InnerHandler = stub }
        );

        using var response = await client.GetAsync(new Uri("https://api.example.com/items"));

        units.ShouldBe(1);
    }

    [Test]
    public async Task SendAsync_FreeRequest_SkipsTheLimiter()
    {
        using var limiter = new AdaptiveRateLimiter(
            AdaptiveRateLimiterTests.NewName(),
            new() { InitialUnitsPerMinute = 600 }
        );
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );
        using var stub = new StubHandler(HttpStatusCode.TooManyRequests);
        using var client = new HttpClient(
            new AdaptiveRateLimitHandler(limiter, new FixedCostEstimator(0)) { InnerHandler = stub }
        );

        using var response = await client.GetAsync(new Uri("https://api.example.com/items"));

        units.ShouldBe(0);
        limiter.UnitsPerMinute.ShouldBe(600);
    }

    [Test]
    public async Task SendAsync_ClassifierFails_DisposesTheResponse()
    {
        using var limiter = new AdaptiveRateLimiter(
            AdaptiveRateLimiterTests.NewName(),
            new(),
            throttleClassifier: new FailingClassifier()
        );
        using var stub = new StubHandler(HttpStatusCode.OK);
        using var client = new HttpClient(
            new AdaptiveRateLimitHandler(limiter) { InnerHandler = stub }
        );

        await Should.ThrowAsync<InvalidOperationException>(() =>
            client.GetAsync(new Uri("https://api.example.com/items"))
        );

        stub.Last!.Content.As<TrackedContent>().Disposed.ShouldBeTrue();
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpResponseMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Last = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new TrackedContent(),
            };
            return Task.FromResult(Last);
        }
    }

    private sealed class TrackedContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingClassifier : IThrottleClassifier
    {
        public ValueTask<ThrottleSignal> ClassifyAsync(
            HttpResponseMessage response,
            RequestCost cost,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("classifier failed");
    }
}

internal static class HttpContentExtensions
{
    public static T As<T>(this HttpContent content)
        where T : HttpContent => (T)content;
}
