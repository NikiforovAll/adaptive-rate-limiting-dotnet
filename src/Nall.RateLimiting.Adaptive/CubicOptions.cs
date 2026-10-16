namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Settings of <see cref="CubicController"/>. The defaults are the constants of the AWS SDK
/// adaptive retry mode.
/// </summary>
public sealed class CubicOptions
{
    /// <summary>
    /// The rate is multiplied by this factor on a throttle. Greater than 0 and less than 1.
    /// </summary>
    public double Beta { get; set; } = 0.7;

    /// <summary>
    /// How fast the rate grows back, in units per second per second cubed, for the whole budget.
    /// Greater than 0.
    /// </summary>
    public double ScaleConstant { get; set; } = 0.4;

    internal CubicOptions Clone() => (CubicOptions)MemberwiseClone();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Beta);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(Beta, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ScaleConstant);
    }
}
