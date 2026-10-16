namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// A request did not get its units in time, or the wait queue was full. The request was not
/// sent.
/// </summary>
public sealed class RateLimitRejectedException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    public RateLimitRejectedException() { }

    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    /// <param name="message">The message.</param>
    public RateLimitRejectedException(string message)
        : base(message) { }

    /// <summary>
    /// Creates the exception with a message and its cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public RateLimitRejectedException(string message, Exception innerException)
        : base(message, innerException) { }
}
