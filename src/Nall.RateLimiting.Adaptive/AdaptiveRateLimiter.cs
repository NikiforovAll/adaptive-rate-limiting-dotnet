using System.Diagnostics.Metrics;

namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// A client-side limiter whose rate follows server feedback. Requests wait for units in an
/// <see cref="AdaptiveTokenBucket"/>; an <see cref="IRateController"/> changes the rate from the
/// responses.
/// </summary>
/// <remarks>
/// A throttled response cuts the rate, at most once in
/// <see cref="AdaptiveRateLimiterOptions.DecreaseCooldown"/>, and its <c>Retry-After</c> holds
/// back all requests for up to <see cref="AdaptiveRateLimiterOptions.MaxPause"/>. A throttle of a
/// request sent before the last cut does not cut again: it says nothing about the new rate. The
/// limiter is thread-safe; share one instance for one budget.
/// </remarks>
public sealed class AdaptiveRateLimiter : IDisposable
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly IRateController? _customController;
    private readonly IThrottleClassifier? _customClassifier;
    private readonly HttpThrottleClassifier _statusClassifier;
    private readonly AdaptiveTokenBucket _bucket;
    private readonly AdaptiveRateLimiterMetrics.Instruments _metrics;
    private readonly List<IDisposable> _owned = [];
    private readonly long _start;
    private AdaptiveRateLimiterOptions _options;
    private IRateController _controller;
    private IDisposable? _instanceSubscription;
    private bool _instanceSourceAttached;
    private int _instanceCount;
    private double _rate;
    private double _rateBeforeLastDecrease;
    private long _lastIncrease;
    private long _lastDecrease;
    private long _growthStart;
    private double _measuredRate;
    private double _sentUnits;
    private double _sentPeriod;
    private bool _decreased;
    private bool _disposed;

    /// <summary>
    /// Creates a limiter.
    /// </summary>
    /// <param name="name">The name of the limiter, for metrics.</param>
    /// <param name="options">The settings. The limiter keeps a copy.</param>
    /// <param name="controller">
    /// The controller. Null for the built-in one that
    /// <see cref="AdaptiveRateLimiterOptions.Controller"/> selects.
    /// </param>
    /// <param name="throttleClassifier">
    /// Tells if a response is a throttle. <see cref="HttpThrottleClassifier"/> if null.
    /// </param>
    /// <param name="timeProvider">The clock. <see cref="TimeProvider.System"/> if null.</param>
    /// <param name="meterFactory">
    /// Creates the meter. If null, the limiter uses a static meter with the same name.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The settings are not valid.</exception>
    public AdaptiveRateLimiter(
        string name,
        AdaptiveRateLimiterOptions options,
        IRateController? controller = null,
        IThrottleClassifier? throttleClassifier = null,
        TimeProvider? timeProvider = null,
        IMeterFactory? meterFactory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        var copy = options.Clone();
        copy.Validate(copy.InstanceCount);

        Name = name;
        _options = copy;
        _instanceCount = copy.InstanceCount;
        _time = timeProvider ?? TimeProvider.System;
        _customController = controller;
        _customClassifier = throttleClassifier;
        _statusClassifier = new HttpThrottleClassifier(_time);
        _controller = controller ?? copy.CreateController();
        _rate = copy.InitialUnitsPerMinute / _instanceCount;
        _rateBeforeLastDecrease = _rate;
        _lastIncrease = _time.GetTimestamp();
        _lastDecrease = _lastIncrease;
        _growthStart = _lastIncrease;
        _start = _lastIncrease;
        _bucket = new AdaptiveTokenBucket(
            BucketRate(_rate),
            BucketBurst(copy),
            Math.Max(0, copy.QueueLimit),
            _time
        );
        _metrics = AdaptiveRateLimiterMetrics.For(meterFactory);
        _metrics.Register(this);
    }

    /// <summary>
    /// The name of the limiter.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The current rate of this process, in units per minute.
    /// </summary>
    public double UnitsPerMinute
    {
        get
        {
            lock (_lock)
            {
                return _rate;
            }
        }
    }

    /// <summary>
    /// The number of processes that share the budget.
    /// </summary>
    public int InstanceCount
    {
        get
        {
            lock (_lock)
            {
                return _instanceCount;
            }
        }
    }

    internal bool Enabled => Volatile.Read(ref _options).Enabled;

    internal int QueuedCount => _bucket.QueuedCount;

    private double MinRate => _options.MinUnitsPerMinute / _instanceCount;

    private double MaxRate => _options.MaxUnitsPerMinute / _instanceCount;

    /// <summary>
    /// Waits until the bucket holds the units of the request, then records them as sent.
    /// </summary>
    /// <param name="cost">The cost of the request. A request of zero units does not wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The send timestamp, to pass to <see cref="OnResponseAsync"/>.</returns>
    /// <exception cref="RateLimitRejectedException">
    /// The queue is full, or the wait is longer than
    /// <see cref="AdaptiveRateLimiterOptions.MaxWait"/>.
    /// </exception>
    public async ValueTask<long> WaitAsync(RequestCost cost, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cost.Units, nameof(cost));

        if (cost.Units == 0)
        {
            return _time.GetTimestamp();
        }

        var options = Volatile.Read(ref _options);
        if (options.Enabled)
        {
            await AcquireAsync(cost.Units, options, cancellationToken).ConfigureAwait(false);
        }

        _metrics.RecordUnits(Name, cost);
        return _time.GetTimestamp();
    }

    /// <summary>
    /// Changes the rate from the response to a request.
    /// </summary>
    /// <remarks>
    /// A server error that is not a throttle does not change the rate: it does not show that
    /// the rate is safe to raise. A disabled limiter, and a request of zero units, only record a
    /// throttle from the status and headers, and do not change the rate.
    /// </remarks>
    /// <param name="response">The response.</param>
    /// <param name="cost">The cost of the request, as passed to <see cref="WaitAsync"/>.</param>
    /// <param name="sentAt">The timestamp that <see cref="WaitAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels a read of the response body.</param>
    /// <returns>A task that completes when the response is recorded.</returns>
    public async ValueTask OnResponseAsync(
        HttpResponseMessage response,
        RequestCost cost,
        long sentAt,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(response);

        var limited = cost.Units > 0 && Enabled;
        if (limited)
        {
            RecordSent(cost.Units);
        }

        var signal =
            limited && _customClassifier is { } classifier
                ? await classifier
                    .ClassifyAsync(response, cost, cancellationToken)
                    .ConfigureAwait(false)
                : _statusClassifier.Classify(response);

        if (signal.IsThrottled)
        {
            _metrics.RecordThrottled(Name, signal, cost.Category);
            if (limited)
            {
                OnThrottled(signal.RetryAfter, sentAt);
            }
        }
        else if (limited && signal.StatusCode < 500)
        {
            OnSuccess();
        }
    }

    /// <summary>
    /// Changes the number of processes that share the budget. The current rate is scaled to the
    /// new share, so the rate found for the whole budget is kept.
    /// </summary>
    /// <param name="instanceCount">The number of processes. One or more.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The count is not positive, or the settings are not valid at the new share. The limiter
    /// keeps the old count.
    /// </exception>
    public void SetInstanceCount(int instanceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instanceCount);

        lock (_lock)
        {
            ApplyInstanceCount(instanceCount);
        }
    }

    /// <summary>
    /// Takes the instance count from <paramref name="source"/> now and each time it changes. A
    /// source replaces the one attached before, and
    /// <see cref="AdaptiveRateLimiterOptions.InstanceCount"/> no longer applies.
    /// </summary>
    /// <remarks>
    /// A later count that is not valid for the settings is ignored, and the limiter keeps the
    /// count it has.
    /// </remarks>
    /// <param name="source">The live instance count.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The current count of the source is not valid for the settings.
    /// </exception>
    public void UseInstanceCount(IInstanceCountSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SetInstanceCount(source.Current);
        var subscription = source.OnChange(count =>
        {
            try
            {
                SetInstanceCount(count);
            }
            catch (ArgumentOutOfRangeException) { }
        });
        lock (_lock)
        {
            _instanceSubscription?.Dispose();
            _instanceSubscription = subscription;
            _instanceSourceAttached = true;
            if (_disposed)
            {
                subscription?.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies new settings. The limiter keeps its rate, clamped to the new limits, and its
    /// queued requests.
    /// </summary>
    /// <param name="options">The new settings. The limiter keeps a copy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The settings are not valid. The limiter keeps the old ones.
    /// </exception>
    public void UpdateOptions(AdaptiveRateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var copy = options.Clone();
        lock (_lock)
        {
            var instanceCount = _instanceSourceAttached ? _instanceCount : copy.InstanceCount;
            copy.Validate(instanceCount);
            var controller = _customController ?? copy.CreateController();

            var previous = _instanceCount;
            Volatile.Write(ref _options, copy);
            _controller = controller;
            _bucket.SetLimits(BucketBurst(copy), Math.Max(0, copy.QueueLimit));
            Rescale(previous, instanceCount);
        }
    }

    /// <summary>
    /// Stops the limiter. Waiting requests fail.
    /// </summary>
    public void Dispose()
    {
        IDisposable[] owned;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owned = [.. _owned];
            _owned.Clear();
            _instanceSubscription?.Dispose();
            _instanceSubscription = null;
        }

        _metrics.Unregister(this);
        _bucket.Dispose();
        foreach (var disposable in owned)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Disposes <paramref name="disposable"/> with the limiter.
    /// </summary>
    internal void Own(IDisposable disposable)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _owned.Add(disposable);
                return;
            }
        }

        disposable.Dispose();
    }

    /// <param name="retryAfter">The <c>Retry-After</c> of the response, if any.</param>
    /// <param name="sentAt">
    /// When the throttled request was sent. A request sent before the last decrease does not
    /// cut the rate again. Its <c>Retry-After</c> still pauses.
    /// </param>
    internal void OnThrottled(TimeSpan? retryAfter = null, long? sentAt = null)
    {
        lock (_lock)
        {
            if (!_options.Enabled)
            {
                return;
            }

            var now = _time.GetTimestamp();
            _lastIncrease = now;
            _bucket.ResetWaited();

            if (
                !_decreased
                || (
                    _time.GetElapsedTime(_lastDecrease, now) >= _options.DecreaseCooldown
                    && (sentAt ?? now) >= _lastDecrease
                )
            )
            {
                _rateBeforeLastDecrease =
                    _measuredRate > 0 ? Math.Min(_rate, _measuredRate) : _rate;
                var state = State(now);
                _decreased = true;
                _lastDecrease = now;
                _growthStart = now;
                SetRate(Math.Min(_controller.OnThrottled(state), _rate));
            }

            if (retryAfter is { } wait && wait > TimeSpan.Zero && _options.MaxPause > TimeSpan.Zero)
            {
                var pause = wait < _options.MaxPause ? wait : _options.MaxPause;
                _bucket.Pause(pause);
                _lastIncrease = now + (long)(pause.TotalSeconds * _time.TimestampFrequency);
                _growthStart = Math.Max(_growthStart, _lastIncrease);
            }
        }
    }

    /// <summary>
    /// Updates the measured send rate, as the AWS SDK adaptive retry mode does (botocore
    /// <c>ClientRateLimiter._update_measured_rate</c>), in units instead of requests.
    /// </summary>
    internal void RecordSent(int units)
    {
        lock (_lock)
        {
            var seconds = _time.GetElapsedTime(_start).TotalSeconds;
            var period = Math.Floor(seconds * 2) / 2;
            _sentUnits += units;
            if (period > _sentPeriod)
            {
                var current = _sentUnits / (period - _sentPeriod) * 60;
                _measuredRate = (current * 0.8) + (_measuredRate * 0.2);
                _sentUnits = 0;
                _sentPeriod = period;
            }
        }
    }

    internal void OnSuccess()
    {
        lock (_lock)
        {
            if (!_options.Enabled)
            {
                return;
            }

            var now = _time.GetTimestamp();
            if (now < _lastIncrease)
            {
                return;
            }

            var next = _controller.OnSuccess(State(now));
            if (next is not { } rate)
            {
                return;
            }

            _lastIncrease = now;
            _bucket.ResetWaited();
            SetRate(rate);
        }
    }

    private static double BucketRate(double unitsPerMinute) =>
        Math.Max(unitsPerMinute / 60, double.Epsilon);

    private static TimeSpan BucketBurst(AdaptiveRateLimiterOptions options) =>
        options.Burst > TimeSpan.Zero ? options.Burst : TimeSpan.FromSeconds(1);

    private RateControllerState State(long now) =>
        new()
        {
            Rate = _rate,
            MinRate = MinRate,
            MaxRate = MaxRate,
            RateBeforeLastDecrease = _rateBeforeLastDecrease,
            SinceLastDecrease =
                now > _growthStart ? _time.GetElapsedTime(_growthStart, now) : TimeSpan.Zero,
            MeasuredRate = _measuredRate,
            SinceLastIncrease = _time.GetElapsedTime(_lastIncrease, now),
            Waited = _bucket.HasWaited,
            InstanceCount = _instanceCount,
        };

    private void ApplyInstanceCount(int instanceCount)
    {
        var previous = _instanceCount;
        if (instanceCount == previous)
        {
            return;
        }

        _options.Validate(instanceCount);
        Rescale(previous, instanceCount);
    }

    private void Rescale(int previous, int instanceCount)
    {
        _instanceCount = instanceCount;
        _rateBeforeLastDecrease = _rateBeforeLastDecrease * previous / instanceCount;
        _measuredRate = _measuredRate * previous / instanceCount;
        SetRate(_rate * previous / instanceCount);
    }

    private void SetRate(double unitsPerMinute)
    {
        var rate = double.IsNaN(unitsPerMinute) ? _rate : unitsPerMinute;
        rate = _options.Enabled ? Math.Clamp(rate, MinRate, Math.Max(MinRate, MaxRate)) : rate;
        if (rate == _rate)
        {
            return;
        }

        _rate = rate;
        _bucket.SetRate(BucketRate(_rate));
    }

    private async ValueTask AcquireAsync(
        int units,
        AdaptiveRateLimiterOptions options,
        CancellationToken cancellationToken
    )
    {
        if (_bucket.TryAcquireNow(units))
        {
            _metrics.RecordWait(Name, TimeSpan.Zero);
            return;
        }

        var start = _time.GetTimestamp();
        using var timeout = new CancellationTokenSource(options.MaxWait, _time);
        using var linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token)
            : null;

        try
        {
            using var lease = await _bucket
                .AcquireAsync(units, linked?.Token ?? timeout.Token)
                .ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                _metrics.RecordRejected(Name, "queue_full");
                throw new RateLimitRejectedException(
                    $"The queue of rate limiter '{Name}' is full ({options.QueueLimit} requests)."
                );
            }
        }
        catch (OperationCanceledException ex)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRejected(Name, "timeout");
            throw new RateLimitRejectedException(
                $"A request waited longer than {options.MaxWait} for {units} units of rate limiter '{Name}'.",
                ex
            );
        }

        _metrics.RecordWait(Name, _time.GetElapsedTime(start));
    }
}
