using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class AdaptiveRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    [Test]
    public void OnThrottled_CutsTheRateOncePerCooldown()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600, MinUnitsPerMinute = 60 });

        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(420);

        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(420);

        _time.Advance(TimeSpan.FromSeconds(5));
        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(294);
    }

    [Test]
    public async Task OnThrottled_CallSentBeforeTheCut_DoesNotCutAgain()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600, MinUnitsPerMinute = 60 });
        var sentBefore = await limiter.WaitAsync(Cost(1), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(420);

        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnThrottled(sentAt: sentBefore);
        limiter.UnitsPerMinute.ShouldBe(420);

        var sentAfter = await limiter.WaitAsync(Cost(1), CancellationToken.None);
        limiter.OnThrottled(sentAt: sentAfter);
        limiter.UnitsPerMinute.ShouldBe(294);
    }

    [Test]
    public void OnThrottled_StopsAtMin()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 80, MinUnitsPerMinute = 60 });

        limiter.OnThrottled();

        limiter.UnitsPerMinute.ShouldBe(60);
    }

    [Test]
    public void OnSuccess_NoCallWaited_KeepsTheRate()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });

        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(600);
    }

    [Test]
    public async Task OnSuccess_CallsWaited_AddsTheStepAfterTheInterval()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        await DrainAndWaitOnce(limiter, bucketSize: 100);

        limiter.OnSuccess();
        limiter.UnitsPerMinute.ShouldBe(600);

        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnSuccess();
        limiter.UnitsPerMinute.ShouldBe(720);
    }

    [Test]
    public async Task OnSuccess_NoWaitInTheLastInterval_KeepsTheRate()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        await DrainAndWaitOnce(limiter, bucketSize: 100);
        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnSuccess();
        limiter.UnitsPerMinute.ShouldBe(720);

        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(720);
    }

    [Test]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.GatewayTimeout)]
    public async Task OnResponseAsync_ServerError_KeepsTheRate(HttpStatusCode status)
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        await DrainAndWaitOnce(limiter, bucketSize: 100);
        _time.Advance(TimeSpan.FromSeconds(10));
        using var response = new HttpResponseMessage(status);

        await limiter.OnResponseAsync(
            response,
            Cost(1),
            _time.GetTimestamp(),
            CancellationToken.None
        );

        limiter.UnitsPerMinute.ShouldBe(600);
    }

    [Test]
    public async Task OnResponseAsync_Throttled_CutsTheRateAndPausesForRetryAfter()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));

        await limiter.OnResponseAsync(
            response,
            Cost(1),
            _time.GetTimestamp(),
            CancellationToken.None
        );
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();

        limiter.UnitsPerMinute.ShouldBe(420);
        _time.Advance(TimeSpan.FromSeconds(2.9));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await waiting;
    }

    [Test]
    public async Task OnResponseAsync_RetryAfterDate_PausesUntilTheDate()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(_time.GetUtcNow().AddSeconds(4));

        await limiter.OnResponseAsync(
            response,
            Cost(1),
            _time.GetTimestamp(),
            CancellationToken.None
        );
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();

        _time.Advance(TimeSpan.FromSeconds(3.9));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await waiting;
    }

    [Test]
    public async Task OnResponseAsync_FreeCall_RecordsTheThrottleButKeepsTheRate()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        var throttled = new List<Dictionary<string, object?>>();
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.ThrottledInstrument,
            (_, tags) => throttled.Add(tags)
        );
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var cost = new RequestCost(0, "other");

        var sentAt = await limiter.WaitAsync(cost, CancellationToken.None);
        await limiter.OnResponseAsync(response, cost, sentAt, CancellationToken.None);

        limiter.UnitsPerMinute.ShouldBe(600);
        var tags = throttled.ShouldHaveSingleItem();
        tags[AdaptiveRateLimiterMetrics.CategoryTag].ShouldBe("other");
        tags["http.response.status_code"].ShouldBe(429);
        tags[AdaptiveRateLimiterMetrics.RetryAfterTag].ShouldBe("none");
    }

    [Test]
    public async Task OnResponseAsync_UsesTheThrottleClassifier()
    {
        using var limiter = new AdaptiveRateLimiter(
            NewName(),
            new() { InitialUnitsPerMinute = 600 },
            throttleClassifier: new AlwaysThrottled(),
            timeProvider: _time
        );
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        await limiter.OnResponseAsync(
            response,
            Cost(1),
            _time.GetTimestamp(),
            CancellationToken.None
        );

        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task InstanceCount_EachProcessTakesAnEqualShare()
    {
        using var limiter = Create(
            new()
            {
                InstanceCount = 2,
                InitialUnitsPerMinute = 1200,
                MinUnitsPerMinute = 200,
                MaxUnitsPerMinute = 1320,
                Aimd = { IncreaseStep = 120 },
            }
        );
        limiter.UnitsPerMinute.ShouldBe(600);
        await DrainAndWaitOnce(limiter, bucketSize: 100);

        _time.Advance(TimeSpan.FromSeconds(10));
        limiter.OnSuccess();
        limiter.UnitsPerMinute.ShouldBe(660);

        for (var i = 0; i < 10; i++)
        {
            limiter.OnThrottled();
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        limiter.UnitsPerMinute.ShouldBe(100);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void InstanceCount_NotPositive_Throws(int instanceCount) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AdaptiveRateLimiter(NewName(), new() { InstanceCount = instanceCount })
        );

    [Test]
    public void SetInstanceCount_ScalesTheCurrentRateAndTheLimits()
    {
        using var limiter = Create(
            new()
            {
                InitialUnitsPerMinute = 1200,
                MinUnitsPerMinute = 200,
                MaxUnitsPerMinute = 1200,
            }
        );
        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(840);

        limiter.SetInstanceCount(2);
        limiter.InstanceCount.ShouldBe(2);
        limiter.UnitsPerMinute.ShouldBe(420);

        limiter.SetInstanceCount(2);
        limiter.UnitsPerMinute.ShouldBe(420);
        limiter.SetInstanceCount(1);
        limiter.UnitsPerMinute.ShouldBe(840);

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            limiter.OnThrottled();
        }
        limiter.SetInstanceCount(2);
        limiter.UnitsPerMinute.ShouldBe(100);
    }

    [Test]
    public void SetInstanceCount_NotPositive_ThrowsAndKeepsTheCount()
    {
        using var limiter = Create(new());

        Should.Throw<ArgumentOutOfRangeException>(() => limiter.SetInstanceCount(0));

        limiter.InstanceCount.ShouldBe(1);
    }

    [Test]
    public void UseInstanceCount_FollowsTheSource()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 1200 });
        var source = new ManualInstanceCountSource(2);

        limiter.UseInstanceCount(source);
        limiter.InstanceCount.ShouldBe(2);
        limiter.UnitsPerMinute.ShouldBe(600);

        source.Set(3);
        limiter.InstanceCount.ShouldBe(3);
        limiter.UnitsPerMinute.ShouldBe(400);

        limiter.Dispose();
        source.Listeners.ShouldBe(0);
    }

    [Test]
    public void UseInstanceCount_StaticSource_SetsTheCount()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 1200 });

        limiter.UseInstanceCount(new StaticInstanceCountSource(4));

        limiter.InstanceCount.ShouldBe(4);
        limiter.UnitsPerMinute.ShouldBe(300);
    }

    [Test]
    public async Task OnSuccess_StopsAtMax()
    {
        using var limiter = Create(
            new() { InitialUnitsPerMinute = 1190, MaxUnitsPerMinute = 1200 }
        );
        await DrainAndWaitOnce(limiter, bucketSize: 198);
        _time.Advance(TimeSpan.FromSeconds(10));

        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(1200);
    }

    [Test]
    public async Task OnThrottled_ResetsTheIncreaseClock()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });

        _time.Advance(TimeSpan.FromSeconds(9));
        limiter.OnThrottled();
        await DrainAndWaitOnce(limiter, bucketSize: 50);
        _time.Advance(TimeSpan.FromSeconds(8));
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task OnThrottled_RetryAfter_HoldsBackAllCallsForThePause()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });

        limiter.OnThrottled(TimeSpan.FromSeconds(20));
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();

        _time.Advance(TimeSpan.FromSeconds(19));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(2));
        await waiting;
    }

    [Test]
    public async Task OnThrottled_RetryAfterAboveMaxPause_PausesForMaxPause()
    {
        using var limiter = Create(
            new() { InitialUnitsPerMinute = 600, MaxPause = TimeSpan.FromSeconds(5) }
        );

        limiter.OnThrottled(TimeSpan.FromMinutes(5));
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();

        _time.Advance(TimeSpan.FromSeconds(4.9));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1.1));
        await waiting;
    }

    [Test]
    public async Task OnThrottled_RateCutDuringPause_DoesNotMakeThePauseLonger()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 1200, MinUnitsPerMinute = 300 });

        limiter.OnThrottled(TimeSpan.FromSeconds(30));
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();
        _time.Advance(TimeSpan.FromSeconds(6));
        limiter.OnThrottled(TimeSpan.FromSeconds(1));
        limiter.UnitsPerMinute.ShouldBe(588);

        _time.Advance(TimeSpan.FromSeconds(23));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(2));
        await waiting;
    }

    [Test]
    public async Task OnSuccess_DuringThePause_KeepsTheRate()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        await DrainAndWaitOnce(limiter, bucketSize: 100);
        limiter.OnThrottled(TimeSpan.FromSeconds(20));

        _time.Advance(TimeSpan.FromSeconds(15));
        limiter.OnSuccess();

        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public async Task WaitAsync_WaitLongerThanMaxWait_Throws()
    {
        using var limiter = Create(
            new() { InitialUnitsPerMinute = 600, MaxWait = TimeSpan.FromSeconds(15) }
        );
        await limiter.WaitAsync(Cost(100), CancellationToken.None);

        var second = limiter.WaitAsync(Cost(100), CancellationToken.None).AsTask();
        var third = limiter.WaitAsync(Cost(100), CancellationToken.None).AsTask();
        _time.Advance(TimeSpan.FromSeconds(10));
        await second;
        _time.Advance(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<RateLimitRejectedException>(third);
    }

    [Test]
    [Arguments(300, 10)]
    [Arguments(60, 10)]
    public void Validate_MaxWaitNotAboveTheLongestWait_Throws(
        int minUnitsPerMinute,
        int maxWaitSeconds
    )
    {
        var options = new AdaptiveRateLimiterOptions
        {
            InitialUnitsPerMinute = 600,
            MinUnitsPerMinute = minUnitsPerMinute,
            MaxWait = TimeSpan.FromSeconds(maxWaitSeconds),
        };

        Should.Throw<ArgumentOutOfRangeException>(() => options.Validate(instanceCount: 1));
    }

    [Test]
    public void Validate_Disabled_DoesNotCheckTheRateSettings()
    {
        var options = new AdaptiveRateLimiterOptions
        {
            Enabled = false,
            MinUnitsPerMinute = 0,
            MaxWait = TimeSpan.Zero,
        };

        Should.NotThrow(() => options.Validate(instanceCount: 1));
    }

    [Test]
    public async Task WaitAsync_QueueFull_Throws()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600, QueueLimit = 0 });
        await limiter.WaitAsync(Cost(100), CancellationToken.None);

        await Should.ThrowAsync<RateLimitRejectedException>(
            limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask()
        );
    }

    [Test]
    public async Task WaitAsync_Rejected_RecordsTheReason()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600, QueueLimit = 0 });
        var reasons = new List<object?>();
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.RejectedInstrument,
            (_, tags) => reasons.Add(tags[AdaptiveRateLimiterMetrics.ReasonTag])
        );
        await limiter.WaitAsync(Cost(100), CancellationToken.None);

        await Should.ThrowAsync<RateLimitRejectedException>(
            limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask()
        );

        reasons.ShouldBe(["queue_full"]);
    }

    [Test]
    public async Task WaitAsync_CallerCanceled_ThrowsOperationCanceled()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        await limiter.WaitAsync(Cost(100), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = limiter.WaitAsync(Cost(1), cts.Token).AsTask();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(waiting);
    }

    [Test]
    public async Task WaitAsync_RecordsUnitsByCategory()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });
        var units = new List<(long Units, object? Category)>();
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            (value, tags) => units.Add((value, tags[AdaptiveRateLimiterMetrics.CategoryTag]))
        );

        await limiter.WaitAsync(new RequestCost(2, "list"), CancellationToken.None);
        await limiter.WaitAsync(Cost(1), CancellationToken.None);

        units.ShouldBe([(2L, (object?)"list"), (1L, "default")]);
    }

    [Test]
    public async Task Disabled_NeverWaitsOrChangesTheRateButRecordsUnits()
    {
        using var limiter = Create(
            new()
            {
                Enabled = false,
                InitialUnitsPerMinute = 60,
                MinUnitsPerMinute = 60,
            }
        );
        var units = 0L;
        using var listener = TestMetrics.Listen(
            limiter,
            AdaptiveRateLimiterMetrics.UnitsInstrument,
            v => units += v
        );

        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitAsync(Cost(100), CancellationToken.None);
        }
        limiter.OnThrottled();

        units.ShouldBe(500);
        limiter.UnitsPerMinute.ShouldBe(60);
    }

    [Test]
    public async Task UpdateOptions_NewLimits_ClampTheRateAndKeepTheState()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 1200 });
        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(840);

        limiter.UpdateOptions(
            new()
            {
                InitialUnitsPerMinute = 600,
                MaxUnitsPerMinute = 600,
                QueueLimit = 0,
            }
        );

        limiter.UnitsPerMinute.ShouldBe(600);
        await limiter.WaitAsync(Cost(100), CancellationToken.None);
        await Should.ThrowAsync<RateLimitRejectedException>(
            limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask()
        );
    }

    [Test]
    public void UpdateOptions_Invalid_ThrowsAndKeepsTheOptions()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 600 });

        Should.Throw<ArgumentOutOfRangeException>(() =>
            limiter.UpdateOptions(new() { MinUnitsPerMinute = 0 })
        );

        limiter.OnThrottled();
        limiter.UnitsPerMinute.ShouldBe(420);
    }

    [Test]
    public void UpdateOptions_InstanceCount_AppliesWithoutASource()
    {
        using var limiter = Create(new() { InitialUnitsPerMinute = 1200 });

        limiter.UpdateOptions(new() { InitialUnitsPerMinute = 1200, InstanceCount = 2 });

        limiter.InstanceCount.ShouldBe(2);
        limiter.UnitsPerMinute.ShouldBe(600);
    }

    [Test]
    public void Constructor_CopiesTheOptions()
    {
        var options = new AdaptiveRateLimiterOptions { InitialUnitsPerMinute = 600 };
        using var limiter = Create(options);

        options.Aimd.DecreaseFactor = 0.5;
        limiter.OnThrottled();

        limiter.UnitsPerMinute.ShouldBe(420);
    }

    internal static RequestCost Cost(int units) => new(units);

    internal static string NewName() => "test-" + Guid.NewGuid().ToString("N");

    private AdaptiveRateLimiter Create(AdaptiveRateLimiterOptions options) =>
        new(NewName(), options, timeProvider: _time);

    private async Task DrainAndWaitOnce(AdaptiveRateLimiter limiter, int bucketSize)
    {
        await limiter.WaitAsync(Cost(bucketSize), CancellationToken.None);
        var waiting = limiter.WaitAsync(Cost(1), CancellationToken.None).AsTask();
        _time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
    }

    private sealed class AlwaysThrottled : IThrottleClassifier
    {
        public ValueTask<ThrottleSignal> ClassifyAsync(
            HttpResponseMessage response,
            RequestCost cost,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new ThrottleSignal(429, null, 1));
    }

    private sealed class ManualInstanceCountSource(int count) : IInstanceCountSource
    {
        private readonly List<Action<int>> _listeners = [];

        public int Current { get; private set; } = count;

        public int Listeners => _listeners.Count;

        public void Set(int count)
        {
            Current = count;
            foreach (var listener in _listeners.ToArray())
            {
                listener(count);
            }
        }

        public IDisposable? OnChange(Action<int> listener)
        {
            _listeners.Add(listener);
            return new Subscription(() => _listeners.Remove(listener));
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
