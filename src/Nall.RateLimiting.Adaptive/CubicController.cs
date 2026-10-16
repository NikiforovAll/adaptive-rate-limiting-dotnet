namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// CUBIC: a throttle multiplies the rate by <see cref="CubicOptions.Beta"/>; then the rate grows
/// back on a cubic curve of the time since the <c>Retry-After</c> pause ended. The curve is flat
/// near the rate that was throttled, and steep far from it.
/// </summary>
/// <remarks>
/// <para>
/// With W the rate at the last decrease, t the time since it, and C the scale constant:
/// K = ∛(W × (1 − β) / C), and the rate is C × (t − K)³ + W. The rate is in units per second
/// in the formula. Growth is capped at two times the measured send rate.
/// </para>
/// <para>
/// The formula, the cap and the defaults come from the AWS SDK adaptive retry mode (botocore
/// <c>CubicCalculator</c> and <c>ClientRateLimiter</c>), which takes them from RFC 8312. Unlike
/// the AWS SDK, the rate goes up only if requests had to wait.
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
        Math.Clamp(
            state.RateBeforeLastDecrease * _beta,
            state.MinRate,
            Math.Max(state.MinRate, state.Rate)
        );

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

        var next = Math.Min(cubic, 2 * state.MeasuredRate);

        return Math.Clamp(Math.Max(state.Rate, next), state.MinRate, state.MaxRate);
    }
}
