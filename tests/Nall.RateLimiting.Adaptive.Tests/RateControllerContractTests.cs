using Microsoft.Extensions.Time.Testing;

namespace Nall.RateLimiting.Adaptive.Tests;

/// <summary>
/// Rules that every built-in controller must keep: the rate stays in its limits, and a throttle
/// never raises it.
/// </summary>
public sealed class RateControllerContractTests
{
    [Test]
    [Arguments(RateControllerType.Aimd)]
    [Arguments(RateControllerType.Cubic)]
    public void Controller_AnyState_KeepsTheRateInItsLimits(RateControllerType type)
    {
        var controller = new AdaptiveRateLimiterOptions { Controller = type }.CreateController();
        var random = new Random(42);

        for (var i = 0; i < 10_000; i++)
        {
            var state = RandomState(random);

            var throttled = controller.OnThrottled(state);
            throttled.ShouldBeInRange(state.MinRate, state.MaxRate);
            throttled.ShouldBeLessThanOrEqualTo(state.Rate);

            if (controller.OnSuccess(state) is { } next)
            {
                next.ShouldBeInRange(state.MinRate, state.MaxRate);
            }
        }
    }

    [Test]
    [Arguments(RateControllerType.Aimd)]
    [Arguments(RateControllerType.Cubic)]
    public async Task Limiter_AnyFeedback_KeepsTheRateInItsLimits(RateControllerType type)
    {
        var time = new FakeTimeProvider();
        var options = new AdaptiveRateLimiterOptions
        {
            Controller = type,
            InitialUnitsPerMinute = 1200,
            MinUnitsPerMinute = 300,
            MaxUnitsPerMinute = 3000,
        };
        using var limiter = new AdaptiveRateLimiter(
            AdaptiveRateLimiterTests.NewName(),
            options,
            timeProvider: time
        );
        var random = new Random(7);
        var raised = false;

        for (var i = 0; i < 300; i++)
        {
            time.Advance(TimeSpan.FromSeconds(random.NextDouble() * 15));
            var before = limiter.UnitsPerMinute;
            if (random.NextDouble() < 0.2)
            {
                limiter.OnThrottled(
                    random.NextDouble() < 0.5 ? TimeSpan.FromSeconds(random.Next(0, 10)) : null
                );
                limiter.UnitsPerMinute.ShouldBeLessThanOrEqualTo(before);
            }
            else
            {
                await DrainAndWaitOnce(limiter, time);
                limiter.OnSuccess();
                raised |= limiter.UnitsPerMinute > before;
            }

            limiter.UnitsPerMinute.ShouldBeInRange(300, 3000);
        }

        raised.ShouldBeTrue();
    }

    private static RateControllerState RandomState(Random random)
    {
        var instances = random.Next(1, 9);
        var min = (1 + (random.NextDouble() * 500)) / instances;
        var max = min + (random.NextDouble() * 5000 / instances);
        return new RateControllerState
        {
            Rate = min + (random.NextDouble() * (max - min)),
            MinRate = min,
            MaxRate = max,
            RateBeforeLastDecrease = min + (random.NextDouble() * (max - min)),
            SinceLastDecrease = TimeSpan.FromSeconds(random.NextDouble() * 120),
            MeasuredRate = random.NextDouble() * 2 * max,
            SinceLastIncrease = TimeSpan.FromSeconds(random.NextDouble() * 30),
            Waited = random.NextDouble() < 0.5,
            InstanceCount = instances,
        };
    }

    private static async Task DrainAndWaitOnce(AdaptiveRateLimiter limiter, FakeTimeProvider time)
    {
        var bucketSize = (int)Math.Ceiling(limiter.UnitsPerMinute / 6);
        await Complete(limiter.WaitAsync(new RequestCost(bucketSize), CancellationToken.None));
        await Complete(limiter.WaitAsync(new RequestCost(1), CancellationToken.None));
        limiter.RecordSent(bucketSize + 1);

        async Task Complete(ValueTask<long> wait)
        {
            var task = wait.AsTask();
            for (var i = 0; i < 100 && limiter.QueuedCount > 0; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }

            limiter.QueuedCount.ShouldBe(0);
            await task;
        }
    }
}
