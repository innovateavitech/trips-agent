using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.Concurrency;
using TripsAgent.Infrastructure.Pricing;

namespace TripsAgent.Infrastructure.Concurrency;

/// <summary>
/// A lease-based lock in Redis: <c>SET key token PX lease NX</c> to take it, a compare-and-delete to give
/// it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token.</b> Each holder writes a random token, and releasing deletes the key only if it still
/// holds that token — in one Lua script, so the check and the delete cannot be separated. Without it, a
/// holder whose lease ran out would delete the lock the next holder had just taken.
/// </para>
/// <para>
/// <b>It fails open, deliberately.</b> If Redis cannot be reached the caller is let through, with an
/// error in the log. A Redis outage must not stop tickets being issued, and it does not make issuing
/// unsafe: this lock is the outermost of four guards (see <c>TicketIssuanceService</c>), and the row
/// lock, the booking's state and the unique index beneath it need no Redis at all.
/// </para>
/// </remarks>
public sealed partial class RedisDistributedLock : IDistributedLock
{
    private const string KeyPrefix = "locks:";

    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLock> _logger;

    public RedisDistributedLock(IConnectionMultiplexer redis, ILogger<RedisDistributedLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var redisKey = new RedisKey(KeyPrefix + key);
        var token = Guid.NewGuid().ToString("N");

        try
        {
            var taken = await _redis.GetDatabase().StringSetAsync(redisKey, token, lease, When.NotExists);
            return taken ? new Handle(this, redisKey, token) : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(_logger, key, ex);
            return NoLockHandle.Instance;
        }
    }

    private async Task ReleaseAsync(RedisKey key, string token)
    {
        try
        {
            await _redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [key], [token]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The lease ends it anyway, a little later than it should have.
            LogReleaseFailed(_logger, key.ToString(), ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Redis could not be reached to take the lock {Key}; carrying on without it. The database guards still hold.")]
    private static partial void LogUnavailable(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Redis could not be reached to release the lock {Key}; it will expire at the end of its lease.")]
    private static partial void LogReleaseFailed(ILogger logger, string key, Exception exception);

    private sealed class Handle(RedisDistributedLock owner, RedisKey key, string token) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            // Released once, however many times it is disposed.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                await owner.ReleaseAsync(key, token);
            }
        }
    }
}

/// <summary>
/// No lock at all, for when no Redis is configured: every caller is let through, and the database
/// guards alone decide. Correct, one layer thinner.
/// </summary>
public sealed partial class NoDistributedLock : IDistributedLock
{
    public NoDistributedLock(ILogger<NoDistributedLock> logger) =>
        LogNoLock(logger ?? throw new ArgumentNullException(nameof(logger)));

    public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable?>(NoLockHandle.Instance);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No Redis connection string is configured (ConnectionStrings:Redis), so there is no distributed lock. "
                  + "Ticket issuance falls back on its database guards alone — safe, one layer thinner. Set it outside local development.")]
    private static partial void LogNoLock(ILogger logger);
}

/// <summary>The handle for a lock that was never taken: disposing it does nothing.</summary>
internal sealed class NoLockHandle : IAsyncDisposable
{
    public static readonly NoLockHandle Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Wires <see cref="IDistributedLock"/>: Redis when configured, no lock when not.</summary>
public static class DistributedLockRegistration
{
    /// <summary>Call after <c>AddPricingCache</c>, whose Redis connection this shares.</summary>
    public static IServiceCollection AddDistributedLocks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(PricingRegistration.RedisConnectionName)))
        {
            services.AddSingleton<IDistributedLock, NoDistributedLock>();
            return services;
        }

        services.AddSingleton<IDistributedLock>(provider => new RedisDistributedLock(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<ILogger<RedisDistributedLock>>()));

        return services;
    }
}
