using System.Threading.RateLimiting;

namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// A token bucket whose fill rate can change at run time. Waiting requests are served in FIFO
/// order.
/// </summary>
/// <remarks>
/// The in-box <see cref="TokenBucketRateLimiter"/> fixes its fill rate in the constructor, and a
/// new limiter on each rate change would drop the queued waiters. A request that costs more than
/// the bucket holds is granted when the bucket is full; the bucket then goes below zero, so the
/// full cost is still charged.
/// </remarks>
public sealed class AdaptiveTokenBucket : RateLimiter
{
    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private TimeSpan _burst;
    private int _queueLimit;
    private double _tokensPerSecond;
    private double _capacity;
    private double _tokens;
    private long _lastRefill;
    private long _pausedUntil;
    private long _successful;
    private long _failed;
    private bool _waited;
    private bool _disposed;

    /// <summary>
    /// Creates a full bucket.
    /// </summary>
    /// <param name="tokensPerSecond">The fill rate. Must be positive.</param>
    /// <param name="burst">The bucket holds the tokens of this period at the current rate.</param>
    /// <param name="queueLimit">The most requests that can wait. Zero means none can wait.</param>
    /// <param name="timeProvider">The clock. <see cref="TimeProvider.System"/> if null.</param>
    public AdaptiveTokenBucket(
        double tokensPerSecond,
        TimeSpan burst,
        int queueLimit,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burst, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(queueLimit);

        _time = timeProvider ?? TimeProvider.System;
        _burst = burst;
        _queueLimit = queueLimit;
        _tokensPerSecond = tokensPerSecond;
        _capacity = CapacityFor(tokensPerSecond);
        _tokens = _capacity;
        _lastRefill = _time.GetTimestamp();
        _timer = _time.CreateTimer(
            static state => ((AdaptiveTokenBucket)state!).Drain(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration => null;

    internal int QueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// True if a request had to wait since the last <see cref="ResetWaited"/>. Does not reset.
    /// </summary>
    internal bool HasWaited
    {
        get
        {
            lock (_lock)
            {
                return _waited;
            }
        }
    }

    /// <summary>
    /// Changes the fill rate. The bucket keeps its tokens, up to the new capacity.
    /// </summary>
    /// <param name="tokensPerSecond">The new fill rate. Must be positive.</param>
    public void SetRate(double tokensPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerSecond);

        lock (_lock)
        {
            Refill();
            _tokensPerSecond = tokensPerSecond;
            _capacity = CapacityFor(tokensPerSecond);
            _tokens = Math.Min(_tokens, _capacity);
            ScheduleDrain();
        }
    }

    /// <summary>
    /// Grants nothing until <paramref name="duration"/> has passed, and then fills from empty.
    /// A pause that ends later than the current one replaces it.
    /// </summary>
    /// <remarks>
    /// The pause is an end time, not a debt of tokens, so a later rate change does not make it
    /// longer.
    /// </remarks>
    /// <param name="duration">How long to grant nothing.</param>
    public void Pause(TimeSpan duration)
    {
        lock (_lock)
        {
            Refill();
            var until =
                _time.GetTimestamp() + (long)(duration.TotalSeconds * _time.TimestampFrequency);
            _pausedUntil = Math.Max(_pausedUntil, until);
            _tokens = Math.Min(_tokens, 0);
            ScheduleDrain();
        }
    }

    /// <summary>
    /// Returns true if a request had to wait since the last call. A request that still waits
    /// counts for the next period too.
    /// </summary>
    /// <returns>True if a request waited since the last call.</returns>
    public bool ResetWaited()
    {
        lock (_lock)
        {
            var waited = _waited;
            _waited = _queue.Count > 0;
            return waited;
        }
    }

    /// <inheritdoc />
    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            Refill();
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = (long)Math.Floor(_tokens),
                CurrentQueuedCount = _queue.Count,
                TotalFailedLeases = _failed,
                TotalSuccessfulLeases = _successful,
            };
        }
    }

    internal void SetLimits(TimeSpan burst, int queueLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burst, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(queueLimit);

        lock (_lock)
        {
            Refill();
            _burst = burst;
            _queueLimit = queueLimit;
            _capacity = CapacityFor(_tokensPerSecond);
            _tokens = Math.Min(_tokens, _capacity);
            ScheduleDrain();
        }
    }

    /// <summary>
    /// Takes the tokens if no request waits and the bucket holds them. A miss does not count as a
    /// failed lease, so the caller can then queue with <see cref="RateLimiter.AcquireAsync"/>.
    /// </summary>
    internal bool TryAcquireNow(int permitCount)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TryTake(permitCount);
        }
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryTake(permitCount))
            {
                return Lease.Acquired;
            }

            _failed++;
            return Lease.Failed;
        }
    }

    /// <inheritdoc />
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken
    )
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryTake(permitCount))
            {
                return ValueTask.FromResult<RateLimitLease>(Lease.Acquired);
            }

            if (_queue.Count >= _queueLimit)
            {
                _failed++;
                return ValueTask.FromResult<RateLimitLease>(Lease.Failed);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var waiter = new Waiter(this, permitCount, cancellationToken);
            _queue.AddLast(waiter.Node);
            _waited = true;
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(
                    static state =>
                    {
                        var w = (Waiter)state!;
                        w.Bucket.Cancel(w);
                    },
                    waiter
                );
            }

            if (_queue.Count == 1)
            {
                ScheduleDrain();
            }

            return new ValueTask<RateLimitLease>(waiter.Completion.Task);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCore();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore()
    {
        DisposeCore();
        return base.DisposeAsyncCore();
    }

    private void DisposeCore()
    {
        List<Waiter> pending;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _queue];
            _queue.Clear();
        }

        _timer.Dispose();
        foreach (var waiter in pending)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetException(
                new ObjectDisposedException(nameof(AdaptiveTokenBucket))
            );
        }
    }

    private double CapacityFor(double tokensPerSecond) =>
        Math.Max(1, tokensPerSecond * _burst.TotalSeconds);

    private bool CanTake(int cost) => _tokens >= Math.Min(cost, _capacity);

    private void Take(int cost)
    {
        _tokens -= cost;
        _successful++;
    }

    private bool TryTake(int cost)
    {
        Refill();
        if (_queue.Count > 0 || !CanTake(cost))
        {
            return false;
        }

        Take(cost);
        return true;
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var from = Math.Max(_lastRefill, _pausedUntil);
        if (now > from)
        {
            var elapsed = _time.GetElapsedTime(from, now);
            _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _tokensPerSecond);
        }

        _lastRefill = now;
    }

    private void ScheduleDrain()
    {
        if (_disposed)
        {
            return;
        }

        if (_queue.First is not { } head)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var paused = Math.Max(
            0,
            (double)(_pausedUntil - _time.GetTimestamp()) / _time.TimestampFrequency
        );
        var missing = Math.Min(head.Value.Cost, _capacity) - _tokens;
        var delay = TimeSpan.FromSeconds(Math.Max(paused + (missing / _tokensPerSecond), 0.001));
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Drain()
    {
        List<Waiter>? granted = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Refill();
            while (_queue.First is { } node && CanTake(node.Value.Cost))
            {
                _queue.RemoveFirst();
                Take(node.Value.Cost);
                (granted ??= []).Add(node.Value);
            }

            ScheduleDrain();
        }

        foreach (var waiter in granted ?? [])
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetResult(Lease.Acquired);
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_lock)
        {
            if (waiter.Node.List is null)
            {
                return;
            }

            var wasHead = waiter.Node == _queue.First;
            _queue.Remove(waiter.Node);
            if (wasHead)
            {
                Refill();
                ScheduleDrain();
            }
        }

        waiter.Completion.TrySetCanceled(waiter.Token);
    }

    private sealed class Waiter
    {
        public Waiter(AdaptiveTokenBucket bucket, int cost, CancellationToken token)
        {
            Bucket = bucket;
            Cost = cost;
            Token = token;
            Node = new LinkedListNode<Waiter>(this);
        }

        public AdaptiveTokenBucket Bucket { get; }

        public int Cost { get; }

        public CancellationToken Token { get; }

        public TaskCompletionSource<RateLimitLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter> Node { get; }

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(bool isAcquired) : RateLimitLease
    {
        public static readonly Lease Acquired = new(true);
        public static readonly Lease Failed = new(false);

        public override bool IsAcquired { get; } = isAcquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
