namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// The strategy that changes the rate from server feedback.
/// </summary>
/// <remarks>
/// A controller is pure: it takes the state and returns the next rate, with no locks and no
/// clock. The limiter owns the rules that every controller needs: the lock, the clock, the
/// clamp to the rate limits, the instance share, the <c>Retry-After</c> pause, the decrease
/// cooldown, and that a throttle of a request sent before the last decrease does not count.
/// The limiter calls a controller under its lock, so a controller is not called concurrently
/// for one limiter.
/// </remarks>
public interface IRateController
{
    /// <summary>
    /// Returns the rate after a throttle.
    /// </summary>
    /// <param name="state">The state of the limiter.</param>
    /// <returns>
    /// The next rate, in units per minute. It must not be above <see cref="RateControllerState.Rate"/>,
    /// and should be in <see cref="RateControllerState.MinRate"/> to
    /// <see cref="RateControllerState.MaxRate"/>; the limiter clamps it.
    /// </returns>
    double OnThrottled(in RateControllerState state);

    /// <summary>
    /// Returns the rate after a response that is not a throttle and not a server error.
    /// </summary>
    /// <param name="state">The state of the limiter.</param>
    /// <returns>
    /// Null to make no decision yet. A rate, the same or a new one, to set it and to restart
    /// <see cref="RateControllerState.SinceLastIncrease"/> and
    /// <see cref="RateControllerState.Waited"/>.
    /// </returns>
    double? OnSuccess(in RateControllerState state);
}
