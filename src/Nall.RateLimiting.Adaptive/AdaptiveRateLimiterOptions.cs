namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Settings of an <see cref="AdaptiveRateLimiter"/>. Rates are in units per minute.
/// </summary>
/// <remarks>
/// The rates are for the whole budget. Each of the <see cref="InstanceCount"/> processes has its
/// own limiter and takes an equal share. The defaults fit a SharePoint app through Microsoft
/// Graph, where a unit is a resource unit.
/// </remarks>
public sealed class AdaptiveRateLimiterOptions
{
    /// <summary>
    /// When false, requests are not limited, but units and throttled responses are still
    /// recorded.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The number of processes that share the budget, until
    /// <see cref="AdaptiveRateLimiter.SetInstanceCount"/> or an
    /// <see cref="IInstanceCountSource"/> sets the live count.
    /// </summary>
    public int InstanceCount { get; set; } = 1;

    /// <summary>
    /// The rate at the start.
    /// </summary>
    public double InitialUnitsPerMinute { get; set; } = 1500;

    /// <summary>
    /// The lowest rate. At the share of one process, a request of
    /// <see cref="MaxRequestUnits"/> must still get its units within <see cref="MaxWait"/>.
    /// </summary>
    public double MinUnitsPerMinute { get; set; } = 200;

    /// <summary>
    /// The highest rate. Set it below the published limit of the server: estimated costs are
    /// not exact, and a cap at the full limit lets the rate climb into a throttle in each long
    /// burst.
    /// </summary>
    public double MaxUnitsPerMinute { get; set; } = 5625;

    /// <summary>
    /// A burst of throttled responses cuts the rate once in this period, not once for each
    /// response.
    /// </summary>
    public TimeSpan DecreaseCooldown { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The bucket holds the units of this period at the current rate.
    /// </summary>
    public TimeSpan Burst { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most requests that can wait for units. Zero means none can wait.
    /// </summary>
    public int QueueLimit { get; set; } = 500;

    /// <summary>
    /// The longest time a request waits for its units before
    /// <see cref="RateLimitRejectedException"/>.
    /// </summary>
    /// <remarks>
    /// The wait runs inside the <see cref="HttpClient"/>, so its timeout also counts the waits of
    /// each retry.
    /// </remarks>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// A throttled response holds back all requests for its <c>Retry-After</c>, up to this
    /// limit.
    /// </summary>
    public TimeSpan MaxPause { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The units of the largest request. Used only to check that <see cref="MaxWait"/> is long
    /// enough at <see cref="MinUnitsPerMinute"/>.
    /// </summary>
    public int MaxRequestUnits { get; set; } = 100;

    /// <summary>
    /// The built-in controller to use, unless a custom <see cref="IRateController"/> is given.
    /// </summary>
    public RateControllerType Controller { get; set; } = RateControllerType.Aimd;

    /// <summary>
    /// Settings of <see cref="AimdController"/>.
    /// </summary>
    public AimdOptions Aimd { get; set; } = new();

    internal AdaptiveRateLimiterOptions Clone()
    {
        var clone = (AdaptiveRateLimiterOptions)MemberwiseClone();
        clone.Aimd = (Aimd ?? new()).Clone();
        return clone;
    }

    /// <summary>
    /// Checks the settings at the share of one of <paramref name="instanceCount"/> processes.
    /// A disabled limiter does not use its rate settings, so they are not checked.
    /// </summary>
    internal void Validate(int instanceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instanceCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(InstanceCount);

        if (!Enabled)
        {
            return;
        }

        var min = MinUnitsPerMinute / instanceCount;
        var max = MaxUnitsPerMinute / instanceCount;
        var initial = InitialUnitsPerMinute / instanceCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(min, nameof(MinUnitsPerMinute));
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min, nameof(MaxUnitsPerMinute));
        ArgumentOutOfRangeException.ThrowIfLessThan(initial, min, nameof(InitialUnitsPerMinute));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initial, max, nameof(InitialUnitsPerMinute));
        ArgumentOutOfRangeException.ThrowIfLessThan(DecreaseCooldown, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Burst, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(QueueLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxWait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPause, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestUnits);
        ArgumentOutOfRangeException.ThrowIfNegative((int)Controller, nameof(Controller));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (int)Controller,
            (int)RateControllerType.Aimd,
            nameof(Controller)
        );

        (Aimd ?? new()).Validate();

        var minPerSecond = min / 60;
        var units = Math.Min(MaxRequestUnits, Math.Max(1, minPerSecond * Burst.TotalSeconds));
        var longestWait = TimeSpan.FromSeconds(units / minPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxWait, longestWait);
    }
}
