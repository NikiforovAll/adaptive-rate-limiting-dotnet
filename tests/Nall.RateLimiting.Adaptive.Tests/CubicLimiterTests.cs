using Microsoft.Extensions.Time.Testing;

namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class CubicLimiterTests
{
    private static readonly double s_k = Math.Cbrt(10 * 0.3 / 0.4);

    private readonly FakeTimeProvider _time = new();

    [Test]
    public async Task OnSuccess_AfterRetryAfterPause_GrowsFromThePauseEnd()
    {
        using var limiter = Create(RateControllerType.Cubic, initial: 600);
        Send(limiter, units: 50, every: 0.5, seconds: 5);

        limiter.OnThrottled(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitOnce(limiter);
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(((0.4 * Math.Pow(1 - s_k, 3)) + 10) * 60, 1e-6);
    }

    [Test]
    public async Task OnSuccess_StopsAtTwiceTheMeasuredRate()
    {
        using var limiter = Create(RateControllerType.Cubic, initial: 600);
        Send(limiter, units: 5, every: 0.5, seconds: 5);

        limiter.OnThrottled();
        _time.Advance(TimeSpan.FromSeconds(20));
        await limiter.WaitAsync(new RequestCost(70), CancellationToken.None);
        await WaitOnce(limiter);
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(1200, 1e-3);
    }

    [Test]
    public void OnThrottled_MeasuredRateBelowTheRate_CutsTheMeasuredRate()
    {
        using var limiter = Create(RateControllerType.Cubic, initial: 1200);
        Send(limiter, units: 5, every: 1, seconds: 10);

        limiter.OnThrottled();

        limiter.UnitsPerMinute.ShouldBe(210, 1e-3);
    }

    [Test]
    public void Aimd_IgnoresTheMeasuredRate()
    {
        using var measured = Create(RateControllerType.Aimd, initial: 1200);
        using var unmeasured = Create(RateControllerType.Aimd, initial: 1200);
        Send(measured, units: 5, every: 1, seconds: 10);
        _time.Advance(TimeSpan.FromSeconds(20));

        measured.OnThrottled();
        unmeasured.OnThrottled();

        measured.UnitsPerMinute.ShouldBe(unmeasured.UnitsPerMinute);
    }

    private AdaptiveRateLimiter Create(RateControllerType type, double initial) =>
        new(
            AdaptiveRateLimiterTests.NewName(),
            new()
            {
                Controller = type,
                InitialUnitsPerMinute = initial,
                MinUnitsPerMinute = 60,
                MaxUnitsPerMinute = 6000,
            },
            timeProvider: _time
        );

    private void Send(AdaptiveRateLimiter limiter, int units, double every, double seconds)
    {
        for (var elapsed = every; elapsed <= seconds; elapsed += every)
        {
            _time.Advance(TimeSpan.FromSeconds(every));
            limiter.RecordSent(units);
        }
    }

    private async Task WaitOnce(AdaptiveRateLimiter limiter)
    {
        var waiting = limiter.WaitAsync(new RequestCost(1), CancellationToken.None).AsTask();
        _time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
    }
}
