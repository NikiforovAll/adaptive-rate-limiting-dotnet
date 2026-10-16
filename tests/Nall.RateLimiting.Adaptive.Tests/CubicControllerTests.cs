namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class CubicControllerTests
{
    private static readonly double s_k = Math.Cbrt(10 * 0.3 / 0.4);

    private readonly CubicController _controller = new();

    [Test]
    public void OnThrottled_MultipliesByBeta() =>
        _controller.OnThrottled(State(rate: 600)).ShouldBe(420, 1e-9);

    [Test]
    public void OnThrottled_UsesTheRateAtTheDecrease() =>
        _controller.OnThrottled(State(rate: 600, rateBeforeLastDecrease: 400)).ShouldBe(280, 1e-9);

    [Test]
    public void OnThrottled_StopsAtMin() =>
        _controller.OnThrottled(State(rate: 250, rateBeforeLastDecrease: 250)).ShouldBe(200);

    [Test]
    public void OnSuccess_NoWait_DoesNotDecide() =>
        _controller.OnSuccess(State(waited: false)).ShouldBeNull();

    [Test]
    public void OnSuccess_RightAfterTheThrottle_KeepsTheRate() =>
        Success(State(sinceLastDecrease: 0)).ShouldBe(420, 1e-6);

    [Test]
    public void OnSuccess_AtK_IsBackAtTheThrottledRate() =>
        Success(State(sinceLastDecrease: s_k)).ShouldBe(600, 1e-3);

    [Test]
    public void OnSuccess_AfterK_GrowsOnTheCubicCurve() =>
        Success(State(sinceLastDecrease: s_k + 2)).ShouldBe(792, 1e-3);

    [Test]
    public void OnSuccess_TwoInstances_GrowsAtHalfTheRate() =>
        Success(
                State(
                    rate: 210,
                    rateBeforeLastDecrease: 300,
                    sinceLastDecrease: s_k + 2,
                    instances: 2
                )
            )
            .ShouldBe(396, 1e-3);

    [Test]
    public void OnSuccess_StopsAtMax() =>
        Success(State(sinceLastDecrease: s_k + 20)).ShouldBe(1000);

    [Test]
    public void OnSuccess_NeverLowersTheRate() =>
        Success(State(rate: 550, sinceLastDecrease: 0.5)).ShouldBe(550);

    [Test]
    public void OnSuccess_StopsAtTwiceTheMeasuredRate() =>
        Success(State(sinceLastDecrease: s_k + 2, measuredRate: 300)).ShouldBe(600);

    [Test]
    public void OnSuccess_NoMeasuredRate_KeepsTheRate() =>
        Success(State(sinceLastDecrease: s_k + 2, measuredRate: 0)).ShouldBe(420);

    [Test]
    public void Constructor_BetaOutOfRange_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CubicController(new CubicOptions { Beta = 1 })
        );

    [Test]
    public void Constructor_ScaleConstantNotPositive_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CubicController(new CubicOptions { ScaleConstant = 0 })
        );

    private double Success(RateControllerState state) => _controller.OnSuccess(state) ?? double.NaN;

    private static RateControllerState State(
        double rate = 420,
        double rateBeforeLastDecrease = 600,
        double sinceLastDecrease = 1,
        bool waited = true,
        int instances = 1,
        double measuredRate = 10_000
    ) =>
        new()
        {
            Rate = rate,
            MinRate = 200 / (double)instances,
            MaxRate = 1000 / (double)instances,
            RateBeforeLastDecrease = rateBeforeLastDecrease,
            SinceLastDecrease = TimeSpan.FromSeconds(sinceLastDecrease),
            MeasuredRate = measuredRate / instances,
            SinceLastIncrease = TimeSpan.FromSeconds(1),
            Waited = waited,
            InstanceCount = instances,
        };
}
