using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace NoisyNeighbor.QuotaApi;

public sealed class QuotaOptions
{
    public double UnitsPerMinute { get; set; } = 3000;

    public TimeSpan Burst { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MinRetryAfter { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The hidden budget that all callers share. The callers do not know its size.
/// </summary>
public sealed class Quota
{
    private static readonly HashSet<string> s_clients = ["worker", "neighbor"];

    private readonly Lock _lock = new();
    private readonly QuotaOptions _options;
    private readonly TimeProvider _time;
    private readonly Counter<long> _requests;
    private double _tokens;
    private long _refilledAt;

    public Quota(IOptions<QuotaOptions> options, TimeProvider time, IMeterFactory meterFactory)
    {
        _options = options.Value;
        _time = time;
        _tokens = Capacity;
        _refilledAt = time.GetTimestamp();

        var meter = meterFactory.Create(Extensions.QuotaApiMeter);
        _requests = meter.CreateCounter<long>(
            "quota.requests",
            "{request}",
            "Requests that reached the server."
        );
        meter.CreateObservableGauge(
            "quota.limit",
            () => _options.UnitsPerMinute,
            "{unit}/min",
            "The hidden budget."
        );
    }

    private double PerSecond => _options.UnitsPerMinute / 60;

    private double Capacity => PerSecond * _options.Burst.TotalSeconds;

    public bool TryTake(string? client, out TimeSpan retryAfter)
    {
        bool taken;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            _tokens = Math.Min(
                Capacity,
                _tokens + (_time.GetElapsedTime(_refilledAt, now).TotalSeconds * PerSecond)
            );
            _refilledAt = now;

            taken = _tokens >= 1;
            if (taken)
            {
                _tokens--;
            }

            var refill = TimeSpan.FromSeconds(Math.Ceiling((1 - _tokens) / PerSecond));
            retryAfter = refill > _options.MinRetryAfter ? refill : _options.MinRetryAfter;
        }

        _requests.Add(
            1,
            new("client", client is not null && s_clients.Contains(client) ? client : "other"),
            new("outcome", taken ? "accepted" : "throttled")
        );
        return taken;
    }
}
