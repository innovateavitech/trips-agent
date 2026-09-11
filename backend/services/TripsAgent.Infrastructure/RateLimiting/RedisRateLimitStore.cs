using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.RateLimiting;

namespace TripsAgent.Infrastructure.RateLimiting;

/// <summary>
/// Request counts in Redis, shared by every API instance.
/// </summary>
/// <remarks>
/// <para>
/// Uses the one <see cref="IConnectionMultiplexer"/> the process already has (registered with the
/// pricing cache) rather than opening a second connection: a multiplexer is designed to be shared,
/// and each one holds its own sockets and background threads.
/// </para>
/// <para>
/// When Redis cannot be reached this answers null and the limiter lets the request through
/// uncounted — see <see cref="RateLimitEvaluator"/> for why failing open is the right call here.
/// </para>
/// </remarks>
public sealed partial class RedisRateLimitStore : IRateLimitStore
{
    /// <summary>
    /// Count, then make sure the window has an end. One script, because Redis runs a script without
    /// interleaving anything else: two instances counting the same caller at the same instant cannot
    /// both see "first request" and both start a window.
    /// </summary>
    /// <remarks>
    /// The expiry is set whenever the key has none (PTTL below zero), not only on the first count,
    /// so a key can never be left counting forever. The window is timed by Redis's clock, not by any
    /// API instance's, so instances whose clocks disagree still agree on when a window ends.
    /// </remarks>
    private const string CountScript =
        """
        local hits = redis.call('INCR', KEYS[1])
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
            ttl = tonumber(ARGV[1])
        end
        return { hits, ttl }
        """;

    /// <summary>While Redis is down, the outage is logged at most this often rather than once per request.</summary>
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _clock;
    private readonly ILogger<RedisRateLimitStore> _logger;

    private long _lastWarningTicks;

    public RedisRateLimitStore(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisRateLimitStore> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RateLimitWindowCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Checked first because a disconnected multiplexer does not fail a command — it queues it
        // until the command times out, five seconds by default. Every request would wait that long
        // for an answer we already know.
        if (!_redis.IsConnected)
        {
            WarnUnavailable(exception: null);
            return null;
        }

        try
        {
            var result = await _redis.GetDatabase().ScriptEvaluateAsync(
                CountScript,
                [new RedisKey(key)],
                [(RedisValue)(long)Math.Ceiling(window.TotalMilliseconds)]);

            var values = (RedisResult[]?)result;

            if (values is not { Length: 2 })
            {
                WarnUnavailable(exception: null);
                return null;
            }

            return new RateLimitWindowCount((long)values[0], TimeSpan.FromMilliseconds((long)values[1]));
        }

        // RedisTimeoutException derives from TimeoutException, not RedisException, so both are named.
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            WarnUnavailable(ex);
            return null;
        }
    }

    private void WarnUnavailable(Exception? exception)
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarningTicks);

        if (now - last < WarningInterval.Ticks)
        {
            return;
        }

        // Only the request that wins the swap logs; any others racing it in the same instant do not.
        if (Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
        {
            LogUnavailable(_logger, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rate limiting is failing open: Redis cannot be reached, so requests are being let "
                  + "through uncounted. Logged at most once a minute while it lasts.")]
    private static partial void LogUnavailable(ILogger logger, Exception? exception);
}
