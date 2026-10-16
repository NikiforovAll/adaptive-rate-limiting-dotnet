namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// The built-in rate controllers.
/// </summary>
public enum RateControllerType
{
    /// <summary>
    /// <see cref="AimdController"/>: additive increase, multiplicative decrease.
    /// </summary>
    Aimd,
}
