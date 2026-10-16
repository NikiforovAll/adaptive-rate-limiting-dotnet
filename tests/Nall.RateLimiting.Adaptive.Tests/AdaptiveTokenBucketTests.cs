using Microsoft.Extensions.Time.Testing;

namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class AdaptiveTokenBucketTests
{
    private readonly FakeTimeProvider _time = new();

    [Test]
    public async Task AcquireAsync_EmptyBucket_GrantsWaitersInOrderAsTokensRefill()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 2);
        (await bucket.AcquireAsync(2)).IsAcquired.ShouldBeTrue();

        var first = bucket.AcquireAsync(1).AsTask();
        var second = bucket.AcquireAsync(1).AsTask();
        first.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromSeconds(1));
        (await first).IsAcquired.ShouldBeTrue();
        second.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromSeconds(1));
        (await second).IsAcquired.ShouldBeTrue();
    }

    [Test]
    public async Task AcquireAsync_QueueFull_FailsAtOnce()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 1, queueLimit: 1);
        (await bucket.AcquireAsync(1)).IsAcquired.ShouldBeTrue();
        var queued = bucket.AcquireAsync(1).AsTask();

        using var lease = await bucket.AcquireAsync(1);

        lease.IsAcquired.ShouldBeFalse();
        queued.IsCompleted.ShouldBeFalse();
    }

    [Test]
    public async Task AcquireAsync_Canceled_LeavesTheQueue()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 1);
        (await bucket.AcquireAsync(1)).IsAcquired.ShouldBeTrue();
        using var cts = new CancellationTokenSource();
        var queued = bucket.AcquireAsync(1, cts.Token).AsTask();

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(queued);
        bucket.GetStatistics()!.CurrentQueuedCount.ShouldBe(0);
    }

    [Test]
    public async Task AttemptAcquire_WithWaiters_Fails()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 1);
        (await bucket.AcquireAsync(1)).IsAcquired.ShouldBeTrue();
        _ = bucket.AcquireAsync(1).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(500));

        using var lease = bucket.AttemptAcquire(0);

        lease.IsAcquired.ShouldBeFalse();
    }

    [Test]
    public async Task AcquireAsync_CostAboveCapacity_GrantsWhenFullAndGoesIntoDebt()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 2);

        (await bucket.AcquireAsync(5)).IsAcquired.ShouldBeTrue();

        var next = bucket.AcquireAsync(1).AsTask();
        _time.Advance(TimeSpan.FromSeconds(3));
        next.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        (await next).IsAcquired.ShouldBeTrue();
    }

    [Test]
    public async Task SetRate_Faster_GrantsTheWaiterSooner()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 10);
        (await bucket.AcquireAsync(10)).IsAcquired.ShouldBeTrue();
        var waiter = bucket.AcquireAsync(5).AsTask();

        bucket.SetRate(5);
        _time.Advance(TimeSpan.FromSeconds(1));

        (await waiter).IsAcquired.ShouldBeTrue();
    }

    [Test]
    public async Task ResetWaited_ReportsOnlyWaitsSinceTheLastCall()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 1);
        (await bucket.AcquireAsync(1)).IsAcquired.ShouldBeTrue();
        bucket.ResetWaited().ShouldBeFalse();

        var waiter = bucket.AcquireAsync(1).AsTask();
        _time.Advance(TimeSpan.FromSeconds(1));
        await waiter;

        bucket.HasWaited.ShouldBeTrue();
        bucket.ResetWaited().ShouldBeTrue();
        bucket.ResetWaited().ShouldBeFalse();
    }

    [Test]
    public async Task Dispose_FailsTheWaiters()
    {
        var bucket = Create(tokensPerSecond: 1, burstSeconds: 1);
        (await bucket.AcquireAsync(1)).IsAcquired.ShouldBeTrue();
        var waiter = bucket.AcquireAsync(1).AsTask();

        await bucket.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(waiter);
    }

    [Test]
    public async Task Cancel_Head_GrantsTheNextWaiterFromTheRefilledTokens()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 10);
        (await bucket.AcquireAsync(10)).IsAcquired.ShouldBeTrue();
        using var cts = new CancellationTokenSource();
        var head = bucket.AcquireAsync(10, cts.Token).AsTask();
        var next = bucket.AcquireAsync(1).AsTask();
        _time.Advance(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        _time.Advance(TimeSpan.FromMilliseconds(10));

        await Should.ThrowAsync<OperationCanceledException>(head);
        next.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Test]
    public async Task Pause_GrantsNothingUntilThePauseEnds()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 10);

        bucket.Pause(TimeSpan.FromSeconds(5));
        var waiter = bucket.AcquireAsync(1).AsTask();

        _time.Advance(TimeSpan.FromSeconds(5));
        waiter.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        (await waiter).IsAcquired.ShouldBeTrue();
    }

    [Test]
    public async Task SetLimits_SmallerBurst_CapsTheTokens()
    {
        using var bucket = Create(tokensPerSecond: 1, burstSeconds: 10);

        bucket.SetLimits(TimeSpan.FromSeconds(2), queueLimit: 10);

        bucket.GetStatistics()!.CurrentAvailablePermits.ShouldBe(2);
        (await bucket.AcquireAsync(2)).IsAcquired.ShouldBeTrue();
        bucket.AttemptAcquire(1).IsAcquired.ShouldBeFalse();
    }

    [Test]
    public void Constructor_RateNotPositive_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AdaptiveTokenBucket(0, TimeSpan.FromSeconds(1), 1, _time)
        );

    private AdaptiveTokenBucket Create(
        double tokensPerSecond,
        double burstSeconds,
        int queueLimit = 10
    ) => new(tokensPerSecond, TimeSpan.FromSeconds(burstSeconds), queueLimit, _time);
}
