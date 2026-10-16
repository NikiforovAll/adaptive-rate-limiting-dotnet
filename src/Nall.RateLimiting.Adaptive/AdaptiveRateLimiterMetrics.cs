using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// The names of the meter, the instruments and the tags of the adaptive limiters.
/// </summary>
/// <remarks>
/// Every measurement has the <see cref="LimiterTag"/> tag with the name of the limiter.
/// </remarks>
public static class AdaptiveRateLimiterMetrics
{
    /// <summary>
    /// The name of the meter. Pass it to <c>AddMeter</c> to collect the metrics.
    /// </summary>
    public const string MeterName = "Nall.RateLimiting.Adaptive";

    /// <summary>
    /// Counter of the units sent. Tags: <see cref="CategoryTag"/>.
    /// </summary>
    public const string UnitsInstrument = "ratelimit.adaptive.units";

    /// <summary>
    /// Counter of throttled responses. A batch adds one for each throttled inner response.
    /// Tags: <c>http.response.status_code</c>, <see cref="RetryAfterTag"/>,
    /// <see cref="CategoryTag"/>.
    /// </summary>
    public const string ThrottledInstrument = "ratelimit.adaptive.throttled";

    /// <summary>
    /// Counter of requests that the limiter did not send. Tags: <see cref="ReasonTag"/>.
    /// </summary>
    public const string RejectedInstrument = "ratelimit.adaptive.rejected";

    /// <summary>
    /// Histogram of the time, in seconds, that a request waited for its units.
    /// </summary>
    public const string WaitInstrument = "ratelimit.adaptive.wait";

    /// <summary>
    /// Gauge of the current rate of the process, in units per minute.
    /// </summary>
    public const string RateInstrument = "ratelimit.adaptive.rate";

    /// <summary>
    /// Gauge of the requests that wait for units.
    /// </summary>
    public const string QueueInstrument = "ratelimit.adaptive.queue";

    /// <summary>
    /// Gauge of the processes that share the budget.
    /// </summary>
    public const string InstancesInstrument = "ratelimit.adaptive.instances";

    /// <summary>
    /// The name of the limiter.
    /// </summary>
    public const string LimiterTag = "ratelimit.limiter";

    /// <summary>
    /// The category of the request, from <see cref="RequestCost.Category"/>, or <c>default</c>.
    /// </summary>
    public const string CategoryTag = "ratelimit.category";

    /// <summary>
    /// The <c>Retry-After</c> of a throttled response: <c>none</c>, <c>le_5s</c>,
    /// <c>le_30s</c> or <c>gt_30s</c>.
    /// </summary>
    public const string RetryAfterTag = "ratelimit.retry_after";

    /// <summary>
    /// Why a request was not sent: <c>queue_full</c> or <c>timeout</c>.
    /// </summary>
    public const string ReasonTag = "ratelimit.reason";

    internal const string StatusCodeTag = "http.response.status_code";

    internal const string DefaultCategory = "default";

    private static readonly Meter s_sharedMeter = new(MeterName);

    private static readonly ConditionalWeakTable<Meter, Instruments> s_byMeter = [];

    internal static Instruments For(IMeterFactory? meterFactory)
    {
        var meter = meterFactory?.Create(new MeterOptions(MeterName)) ?? s_sharedMeter;
        return s_byMeter.GetValue(meter, static m => new Instruments(m));
    }

    internal static string RetryAfterBucket(TimeSpan? retryAfter) =>
        retryAfter switch
        {
            null => "none",
            { TotalSeconds: <= 5 } => "le_5s",
            { TotalSeconds: <= 30 } => "le_30s",
            _ => "gt_30s",
        };

    internal sealed class Instruments
    {
        private readonly Lock _lock = new();
        private readonly List<AdaptiveRateLimiter> _limiters = [];
        private readonly Counter<long> _units;
        private readonly Counter<long> _throttled;
        private readonly Counter<long> _rejected;
        private readonly Histogram<double> _wait;

        public Instruments(Meter meter)
        {
            _units = meter.CreateCounter<long>(
                UnitsInstrument,
                "{unit}",
                "Units sent through the adaptive rate limiter."
            );
            _throttled = meter.CreateCounter<long>(
                ThrottledInstrument,
                "{response}",
                "Responses that throttled the request."
            );
            _rejected = meter.CreateCounter<long>(
                RejectedInstrument,
                "{request}",
                "Requests that the adaptive rate limiter did not send."
            );
            _wait = meter.CreateHistogram<double>(
                WaitInstrument,
                "s",
                "Time that a request waited for units."
            );
            meter.CreateObservableGauge(
                RateInstrument,
                () => Observe(static l => l.UnitsPerMinute),
                "{unit}/min",
                "Current rate of the adaptive rate limiter in this process."
            );
            meter.CreateObservableGauge(
                QueueInstrument,
                () => Observe(static l => l.QueuedCount),
                "{request}",
                "Requests that wait for units."
            );
            meter.CreateObservableGauge(
                InstancesInstrument,
                () => Observe(static l => l.InstanceCount),
                "{process}",
                "Processes that share the budget."
            );
        }

        public void Register(AdaptiveRateLimiter limiter)
        {
            lock (_lock)
            {
                _limiters.Add(limiter);
            }
        }

        public void Unregister(AdaptiveRateLimiter limiter)
        {
            lock (_lock)
            {
                _limiters.Remove(limiter);
            }
        }

        public void RecordUnits(string limiter, RequestCost cost) =>
            _units.Add(
                cost.Units,
                new KeyValuePair<string, object?>(LimiterTag, limiter),
                new KeyValuePair<string, object?>(CategoryTag, cost.Category ?? DefaultCategory)
            );

        public void RecordThrottled(string limiter, ThrottleSignal signal, string? category) =>
            _throttled.Add(
                signal.ThrottledCount,
                new KeyValuePair<string, object?>(LimiterTag, limiter),
                new KeyValuePair<string, object?>(StatusCodeTag, signal.StatusCode),
                new KeyValuePair<string, object?>(
                    RetryAfterTag,
                    RetryAfterBucket(signal.RetryAfter)
                ),
                new KeyValuePair<string, object?>(CategoryTag, category ?? DefaultCategory)
            );

        public void RecordRejected(string limiter, string reason) =>
            _rejected.Add(
                1,
                new KeyValuePair<string, object?>(LimiterTag, limiter),
                new KeyValuePair<string, object?>(ReasonTag, reason)
            );

        public void RecordWait(string limiter, TimeSpan wait) =>
            _wait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>(LimiterTag, limiter));

        private List<Measurement<T>> Observe<T>(Func<AdaptiveRateLimiter, T> read)
            where T : struct
        {
            AdaptiveRateLimiter[] limiters;
            lock (_lock)
            {
                limiters = [.. _limiters];
            }

            var measurements = new List<Measurement<T>>(limiters.Length);
            foreach (var limiter in limiters)
            {
                if (limiter.Enabled)
                {
                    measurements.Add(
                        new Measurement<T>(
                            read(limiter),
                            new KeyValuePair<string, object?>(LimiterTag, limiter.Name)
                        )
                    );
                }
            }

            return measurements;
        }
    }
}
