namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Tells how many processes share one budget. Each process takes an equal share.
/// </summary>
/// <remarks>
/// Implement it for a live count, such as Kubernetes replicas or job server heartbeats.
/// </remarks>
public interface IInstanceCountSource
{
    /// <summary>
    /// The current number of processes. One or more.
    /// </summary>
    int Current { get; }

    /// <summary>
    /// Calls <paramref name="listener"/> with the new count each time it changes.
    /// </summary>
    /// <param name="listener">Gets the new count.</param>
    /// <returns>Stops the calls when disposed, or null if the count never changes.</returns>
    IDisposable? OnChange(Action<int> listener);
}
