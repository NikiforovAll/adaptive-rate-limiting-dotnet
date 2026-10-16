namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Additive increase, multiplicative decrease. The default controller.
/// </summary>
/// <remarks>
/// A throttle multiplies the rate by <see cref="AimdOptions.DecreaseFactor"/>. A period of
/// <see cref="AimdOptions.IncreaseInterval"/> with no throttle, in which requests had to wait,
/// adds <see cref="AimdOptions.IncreaseStep"/>. If no request waited, the rate stays: it was not
/// tested, so it is not safe to raise.
/// </remarks>
public sealed class AimdController : IRateController
{
    private readonly double _decreaseFactor;
    private readonly double _increaseStep;
    private readonly TimeSpan _increaseInterval;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="options">The settings. The defaults if null.</param>
    public AimdController(AimdOptions? options = null)
    {
        options ??= new AimdOptions();
        options.Validate();

        _decreaseFactor = options.DecreaseFactor;
        _increaseStep = options.IncreaseStep;
        _increaseInterval = options.IncreaseInterval;
    }

    /// <inheritdoc />
    public double OnThrottled(in RateControllerState state) =>
        Math.Max(state.Rate * _decreaseFactor, state.MinRate);

    /// <inheritdoc />
    public double? OnSuccess(in RateControllerState state)
    {
        if (state.SinceLastIncrease < _increaseInterval)
        {
            return null;
        }

        if (state.Waited && state.Rate < state.MaxRate)
        {
            return Math.Min(
                state.Rate + (_increaseStep / Math.Max(1, state.InstanceCount)),
                state.MaxRate
            );
        }

        return state.Rate;
    }
}
