namespace Nall.RateLimiting.Adaptive.Tests;

public sealed class AimdControllerTests
{
    private readonly AimdController _controller = new(
        new AimdOptions
        {
            DecreaseFactor = 0.5,
            IncreaseStep = 100,
            IncreaseInterval = TimeSpan.FromSeconds(10),
        }
    );

    [Test]
    public void OnThrottled_MultipliesByTheFactor() =>
        _controller.OnThrottled(State(rate: 800)).ShouldBe(400);

    [Test]
    public void OnThrottled_StopsAtMin() => _controller.OnThrottled(State(rate: 300)).ShouldBe(200);

    [Test]
    public void OnSuccess_BeforeTheInterval_DoesNotDecide() =>
        _controller.OnSuccess(State(sinceLastIncrease: TimeSpan.FromSeconds(9))).ShouldBeNull();

    [Test]
    public void OnSuccess_NoWait_KeepsTheRate() =>
        _controller.OnSuccess(State(rate: 800, waited: false)).ShouldBe(800);

    [Test]
    public void OnSuccess_Waited_AddsTheStep() =>
        _controller.OnSuccess(State(rate: 800)).ShouldBe(900);

    [Test]
    public void OnSuccess_TwoInstances_AddsHalfTheStep() =>
        _controller.OnSuccess(State(rate: 800, instanceCount: 2)).ShouldBe(850);

    [Test]
    public void OnSuccess_StopsAtMax() => _controller.OnSuccess(State(rate: 950)).ShouldBe(1000);

    [Test]
    public void Constructor_FactorOutOfRange_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AimdController(new AimdOptions { DecreaseFactor = 1 })
        );

    private static RateControllerState State(
        double rate = 500,
        bool waited = true,
        TimeSpan? sinceLastIncrease = null,
        int instanceCount = 1
    ) =>
        new()
        {
            Rate = rate,
            MinRate = 200,
            MaxRate = 1000,
            RateBeforeLastDecrease = rate,
            SinceLastDecrease = TimeSpan.FromMinutes(1),
            SinceLastIncrease = sinceLastIncrease ?? TimeSpan.FromSeconds(10),
            Waited = waited,
            InstanceCount = instanceCount,
        };
}
