namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// CUBIC: a throttle multiplies the rate by <see cref="CubicOptions.Beta"/>; then the rate grows
/// back on a cubic curve of the time since the throttle. The curve is flat near the rate that
/// was throttled, and steep far from it.
/// </summary>
/// <remarks>
/// <para>
/// With W the rate before the last decrease, t the time since it, and C the scale constant:
/// K = ∛(W × (1 − β) / C), and the rate is C × (t − K)³ + W. The rate is in units per second
/// in the formula.
/// </para>
/// <para>
/// The formula and the defaults come from the AWS SDK adaptive retry mode (botocore
/// <c>CubicCalculator</c>), which takes them from RFC 8312. Unlike the AWS SDK, W is the
/// current rate, not a measured send rate, and the rate goes up only if requests had to wait.
/// </para>
/// </remarks>
public sealed class CubicController : IRateController
{
    private readonly double _beta;
    private readonly double _scaleConstant;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="options">The settings. The defaults if null.</param>
    public CubicController(CubicOptions? options = null)
    {
        options ??= new CubicOptions();
        options.Validate();

        _beta = options.Beta;
        _scaleConstant = options.ScaleConstant;
    }

    /// <inheritdoc />
    public double OnThrottled(in RateControllerState state) =>
        Math.Clamp(state.Rate * _beta, state.MinRate, Math.Max(state.MinRate, state.Rate));

    /// <inheritdoc />
    public double? OnSuccess(in RateControllerState state)
    {
        if (!state.Waited)
        {
            return null;
        }

        if (state.Rate >= state.MaxRate)
        {
            return state.MaxRate;
        }

        var scale = _scaleConstant / Math.Max(1, state.InstanceCount);
        var maxPerSecond = state.RateBeforeLastDecrease / 60;
        var k = Math.Cbrt(maxPerSecond * (1 - _beta) / scale);
        var t = state.SinceLastDecrease.TotalSeconds;
        var cubic = ((scale * Math.Pow(t - k, 3)) + maxPerSecond) * 60;

        return Math.Clamp(Math.Max(state.Rate, cubic), state.MinRate, state.MaxRate);
    }
}
