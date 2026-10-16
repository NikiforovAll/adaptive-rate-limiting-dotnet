namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// An instance count that never changes.
/// </summary>
public sealed class StaticInstanceCountSource : IInstanceCountSource
{
    /// <summary>
    /// Creates the source.
    /// </summary>
    /// <param name="count">The number of processes. One or more.</param>
    public StaticInstanceCountSource(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        Current = count;
    }

    /// <inheritdoc />
    public int Current { get; }

    /// <inheritdoc />
    public IDisposable? OnChange(Action<int> listener) => null;
}
