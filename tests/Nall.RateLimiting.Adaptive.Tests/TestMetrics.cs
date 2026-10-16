using System.Diagnostics.Metrics;

namespace Nall.RateLimiting.Adaptive.Tests;

internal static class TestMetrics
{
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
