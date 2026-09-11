using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.Search;
using TripsAgent.Infrastructure.Pricing;

namespace TripsAgent.Infrastructure.Search;

/// <summary>
/// Wires the search result cache (#40) — Redis when configured, nothing when not — and its lifetimes.
/// </summary>
public static class SearchRegistration
{
    public static IServiceCollection AddSearchCache(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ReadOptions(configuration));

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(PricingRegistration.RedisConnectionName)))
        {
            // Every search asks the supplier. Slower, never wrong.
            services.AddSingleton<ISearchResultCache, NoSearchResultCache>();
            return services;
        }

        // The multiplexer AddPricingCache registered: one connection per process, shared by both caches.
        services.AddSingleton<ISearchResultCache>(provider => new RedisSearchResultCache(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<ILogger<RedisSearchResultCache>>()));

        return services;
    }

    /// <summary>
    /// <c>Search:CacheMinutes</c> and <c>Search:ResultMinutes</c>. A bad value stops startup rather than
    /// falling back: a typo that silently turned the cache into an hour-long one would serve stale fares.
    /// </summary>
    private static SearchOptions ReadOptions(IConfiguration configuration)
    {
        var defaults = new SearchOptions();

        return new SearchOptions
        {
            CacheLifetime = Minutes(configuration, "CacheMinutes", max: 30) ?? defaults.CacheLifetime,
            ResultLifetime = Minutes(configuration, "ResultMinutes", max: 60) ?? defaults.ResultLifetime,
        };
    }

    private static TimeSpan? Minutes(IConfiguration configuration, string name, int max)
    {
        var key = $"{SearchOptions.SectionName}:{name}";
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1 || minutes > max)
        {
            throw new InvalidOperationException($"{key} must be a whole number of minutes from 1 to {max}. It was '{value}'.");
        }

        return TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>
/// Net search results in Redis, per agency and criteria, for a few minutes (#40).
/// </summary>
/// <remarks>
/// <para>
/// <b>Failures are misses.</b> A Redis outage makes search ask the supplier every time — slower,
/// never wrong. The same goes for an entry that no longer deserialises: it is treated as absent,
/// not as an error the agent sees.
/// </para>
/// <para>
/// Only net figures are stored; see <see cref="NetSearch"/>. The agency is in the key, so one
/// agency's search is never another's.
/// </para>
/// </remarks>
public sealed partial class RedisSearchResultCache : ISearchResultCache
{
    /// <summary>Bump the version whenever <see cref="NetSearch"/>'s shape changes.</summary>
    private const string KeyPrefix = "search:results:v1:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisSearchResultCache> _logger;

    public RedisSearchResultCache(IConnectionMultiplexer redis, ILogger<RedisSearchResultCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>The key for one agency's search. Public so a test can prove two agencies never share one.</summary>
    public static string KeyFor(Guid agencyId, string criteriaHash) => $"{KeyPrefix}{agencyId:N}:{criteriaHash}";

    public async Task<NetSearch?> GetAsync(Guid agencyId, string criteriaHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var stored = await _redis.GetDatabase().StringGetAsync(KeyFor(agencyId, criteriaHash));
            return stored.HasValue ? Deserialize(stored!) : null;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogReadFailed(_logger, agencyId, ex);
            return null;
        }
    }

    public async Task SetAsync(
        Guid agencyId,
        string criteriaHash,
        NetSearch search,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        try
        {
            await _redis.GetDatabase().StringSetAsync(
                KeyFor(agencyId, criteriaHash), JsonSerializer.Serialize(search, Json), lifetime);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogWriteFailed(_logger, agencyId, ex);
        }
    }

    private static NetSearch? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetSearch>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsRedisFailure(Exception ex) =>
        ex is RedisException or TimeoutException or ObjectDisposedException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search cache read failed for agency {AgencyId}; searching the supplier instead")]
    private static partial void LogReadFailed(ILogger logger, Guid agencyId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search cache write failed for agency {AgencyId}; the next search will ask the supplier again")]
    private static partial void LogWriteFailed(ILogger logger, Guid agencyId, Exception exception);
}
