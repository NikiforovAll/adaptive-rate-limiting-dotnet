namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Settings of <see cref="AimdController"/>.
/// </summary>
public sealed class AimdOptions
{
    /// <summary>
    /// The rate is multiplied by this factor on a throttle. Greater than 0 and less than 1.
    /// </summary>
    public double DecreaseFactor { get; set; } = 0.7;

    /// <summary>
    /// The units per minute added for the whole budget, after a quiet
    /// <see cref="IncreaseInterval"/>. Each process adds its share.
    /// </summary>
    public double IncreaseStep { get; set; } = 120;

    /// <summary>
    /// The rate goes up by <see cref="IncreaseStep"/> after this period with no throttle, and
    /// only if requests had to wait in it.
    /// </summary>
    public TimeSpan IncreaseInterval { get; set; } = TimeSpan.FromSeconds(10);

    internal AimdOptions Clone() => (AimdOptions)MemberwiseClone();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DecreaseFactor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(DecreaseFactor, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(IncreaseStep);
        ArgumentOutOfRangeException.ThrowIfLessThan(IncreaseInterval, TimeSpan.Zero);
    }
}
