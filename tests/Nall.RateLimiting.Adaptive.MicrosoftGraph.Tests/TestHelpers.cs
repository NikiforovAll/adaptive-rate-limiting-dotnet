using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

internal static class TestHelpers
{
    public static string NewName() => $"test-{Guid.NewGuid():N}";

    public static HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    public static MeterListener Listen(
        AdaptiveRateLimiter limiter,
        string instrument,
        Action<long> onValue
    ) => Listen(limiter, instrument, (value, _) => onValue(value));

    /// <summary>
    /// Listens to one instrument of one limiter. Tests run in parallel on a shared meter, so the
    /// listener keeps only the measurements tagged with the name of the limiter.
    /// </summary>
    public static MeterListener Listen(
        AdaptiveRateLimiter limiter,
        string instrument,
        Action<long, Dictionary<string, object?>> onMeasurement
    )
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == AdaptiveRateLimiterMetrics.MeterName && i.Name == instrument)
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, tags, _) =>
            {
                var map = new Dictionary<string, object?>(tags.ToArray());
                if (
                    Equals(
                        map.GetValueOrDefault(AdaptiveRateLimiterMetrics.LimiterTag),
                        limiter.Name
                    )
                )
                {
                    onMeasurement(value, map);
                }
            }
        );
        listener.Start();
        return listener;
    }
}

internal sealed class StubHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private readonly List<string> _bodies = [];
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
            {
                return [.. _bodies];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        if (request.Content is { } content)
        {
            var body = await content.ReadAsStringAsync(cancellationToken);
            lock (_bodies)
            {
                _bodies.Add(body);
            }
        }

        var response = responses[Math.Min(index, responses.Length - 1)]();
        response.RequestMessage = request;
        return response;
    }
}
