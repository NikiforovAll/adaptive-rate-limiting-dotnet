namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// The state of a limiter that a controller reads. Rates are in units per minute for this
/// process, after the instance share.
/// </summary>
public readonly record struct RateControllerState
{
    /// <summary>
    /// The current rate.
    /// </summary>
    public double Rate { get; init; }

    /// <summary>
    /// The lowest rate the limiter allows.
    /// </summary>
    public double MinRate { get; init; }

    /// <summary>
    /// The highest rate the limiter allows.
    /// </summary>
    public double MaxRate { get; init; }

    /// <summary>
    /// The rate just before the last decrease. Before the first decrease, the initial rate.
    /// </summary>
    public double RateBeforeLastDecrease { get; init; }

    /// <summary>
    /// The time since the last decrease. Before the first decrease, the time since the limiter
    /// started.
    /// </summary>
    public TimeSpan SinceLastDecrease { get; init; }

    /// <summary>
    /// The time since the controller last returned a rate from
    /// <see cref="IRateController.OnSuccess"/>, or since the last throttle, or since the end of
    /// the last <c>Retry-After</c> pause, whichever is latest.
    /// </summary>
    public TimeSpan SinceLastIncrease { get; init; }

    /// <summary>
    /// True if a request had to wait for units in that time. A rate that no request waits for
    /// is not tested, so most controllers do not raise it.
    /// </summary>
    public bool Waited { get; init; }

    /// <summary>
    /// The number of processes that share the budget. The rates are already divided by it; a
    /// controller divides its own additive steps by it.
    /// </summary>
    public int InstanceCount { get; init; }
}
