using Nall.RateLimiting.Adaptive;
using StackExchange.Redis;

namespace NoisyNeighbor.Worker;

/// <summary>
/// Each worker writes a heartbeat to a Redis sorted set. The count is the number of workers
/// that wrote one in the last <see cref="s_ttl"/>.
/// </summary>
public sealed class RedisInstanceCount(
    IConnectionMultiplexer redis,
    ILogger<RedisInstanceCount> logger
) : IInstanceCountSource
{
    private static readonly RedisKey s_key = "noisy-neighbor:workers";
    private static readonly TimeSpan s_period = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_ttl = TimeSpan.FromSeconds(15);

    private readonly string _id = $"{Environment.MachineName}:{Environment.ProcessId}";
    private int _current = 1;

    public int Current => _current = Beat();

    public IDisposable? OnChange(Action<int> listener) =>
        new Timer(
            _ =>
            {
                try
                {
                    listener(_current = Beat());
                }
                catch (RedisException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Redis is not available. Keep {Count} workers.",
                        _current
                    );
                }
            },
            null,
            s_period,
            s_period
        );

    private int Beat()
    {
        var db = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.SortedSetAdd(s_key, _id, now);
        db.SortedSetRemoveRangeByScore(
            s_key,
            double.NegativeInfinity,
            now - s_ttl.TotalMilliseconds
        );
        return Math.Max(1, (int)db.SortedSetLength(s_key));
    }
}
