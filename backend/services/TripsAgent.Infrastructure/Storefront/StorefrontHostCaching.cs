using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>How long a resolved hostname is remembered.</summary>
public sealed class StorefrontHostCacheOptions
{
    public const string SectionName = "Storefront:HostCache";

    /// <summary>
    /// How long an entry lives. Also the longest a domain change can go unseen if the invalidation
    /// itself fails, so it is kept short; invalidation, not expiry, is what normally shows a change.
    /// </summary>
    public TimeSpan EntryLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long "no site answers here" is remembered. Shorter, because an agency that has just
    /// pointed a name at us should not have to wait out a full lifetime to see it work — and a
    /// minute is already long enough to absorb a scanner's thousand tries.
    /// </summary>
    public TimeSpan MissLifetime { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Keeps host-to-agency routing in Redis, versioned by one generation number for the whole map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generation rather than deleting keys.</b> The same race <c>RedisMarkupRuleCache</c>
/// describes: a request that has already read the old row can write it back after an invalidation
/// deleted the key, hiding the change until the entry expires. Bumping a generation instead makes
/// that stale write land under a generation nobody reads any more.
/// </para>
/// <para>
/// <b>One generation for every hostname, not one each.</b> The map is small and changes rarely, and
/// a change to one hostname usually touches another — promoting a new primary changes the canonical
/// address cached against every other hostname of that site. Dropping the lot is both simpler and
/// more correct than working out which entries a change reached.
/// </para>
/// </remarks>
public sealed partial class RedisStorefrontHostCache : IStorefrontHostCache
{
    /// <summary>Bump the version segment whenever the stored JSON shape changes.</summary>
    private const string KeyPrefix = "storefront:host:v1:";

    /// <summary>What a cached miss looks like. Distinct from "nothing cached", which is an empty value.</summary>
    private const string MissMarker = "-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly StorefrontHostCacheOptions _options;
    private readonly ILogger<RedisStorefrontHostCache> _logger;

    public RedisStorefrontHostCache(
        IConnectionMultiplexer redis,
        StorefrontHostCacheOptions options,
        ILogger<RedisStorefrontHostCache> logger)
    {
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    /// <summary>The key holding the current generation of the whole map.</summary>
    public static string GenerationKey => KeyPrefix + "gen";

    public async Task<StorefrontHostRoute?> GetOrLoadAsync(
        string hostname,
        Func<CancellationToken, Task<StorefrontHostRoute?>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentNullException.ThrowIfNull(load);

        var redis = _redis.GetDatabase();
        long generation;

        try
        {
            var stored = await redis.StringGetAsync(GenerationKey);
            generation = stored.HasValue ? (long)stored : 0;

            var cached = await redis.StringGetAsync(EntryKey(hostname, generation));

            if (cached.HasValue)
            {
                return cached == MissMarker ? null : Deserialize(cached!);
            }
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogReadFailed(_logger, hostname, ex);
            return await load(cancellationToken);
        }

        var loaded = await load(cancellationToken);

        try
        {
            await redis.StringSetAsync(
                EntryKey(hostname, generation),
                loaded is null ? MissMarker : JsonSerializer.Serialize(CachedRoute.From(loaded), Json),
                loaded is null ? _options.MissLifetime : _options.EntryLifetime);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogWriteFailed(_logger, hostname, ex);
        }

        return loaded;
    }

    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().StringIncrementAsync(GenerationKey);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // The domain change is already committed. Failing now would say it did not save when it
            // did; logged as an error because a hostname can keep its old routing until entries expire.
            LogInvalidationFailed(_logger, _options.EntryLifetime, ex);
        }
    }

    private static string EntryKey(string hostname, long generation) =>
        string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}g{generation}:{hostname}");

    // RedisTimeoutException derives from TimeoutException, not RedisException, so both are named.
    private static bool IsRedisFailure(Exception ex) => ex is RedisException or TimeoutException;

    private static StorefrontHostRoute? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedRoute>(json, Json)?.ToRoute();
        }
        catch (JsonException)
        {
            // An entry written by an older shape under the same key version. Treated as a miss.
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Storefront host cache read failed for {Hostname}; resolving from the database instead.")]
    private static partial void LogReadFailed(ILogger logger, string hostname, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Storefront host cache write failed for {Hostname}; the next request resolves from the database again.")]
    private static partial void LogWriteFailed(ILogger logger, string hostname, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Storefront host cache invalidation failed. The domain change is saved, but hostnames may keep "
                  + "their previous routing for up to {EntryLifetime}.")]
    private static partial void LogInvalidationFailed(ILogger logger, TimeSpan entryLifetime, Exception exception);

    /// <summary>The stored shape: flat and explicit, so the JSON does not follow the record's shape.</summary>
    private sealed record CachedRoute(Guid AgencyId, Guid SiteId, string Hostname, string PrimaryHostname, bool Serveable)
    {
        public static CachedRoute From(StorefrontHostRoute route) =>
            new(route.AgencyId, route.SiteId, route.Hostname, route.PrimaryHostname, route.Serveable);

        public StorefrontHostRoute ToRoute() => new(AgencyId, SiteId, Hostname, PrimaryHostname, Serveable);
    }
}

/// <summary>
/// What runs where no Redis is configured: every lookup goes to the database.
/// </summary>
/// <remarks>
/// Correct, just slower — the storefront works on a laptop with nothing else running. Production
/// has Redis, and <c>StorefrontRegistration</c> picks the Redis adapter whenever it is configured.
/// </remarks>
public sealed class UncachedStorefrontHostCache : IStorefrontHostCache
{
    public Task<StorefrontHostRoute?> GetOrLoadAsync(
        string hostname,
        Func<CancellationToken, Task<StorefrontHostRoute?>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);

        return load(cancellationToken);
    }

    public Task InvalidateAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
