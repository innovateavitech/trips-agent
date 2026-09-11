using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.Pricing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Infrastructure.Pricing;

/// <summary>Settings for the markup rule cache.</summary>
public sealed class MarkupRuleCacheOptions
{
    public const string SectionName = "Pricing";

    /// <summary>
    /// How long an entry lives. It is also the longest a rule change can go unseen if the
    /// invalidation itself fails — Redis unreachable at exactly the wrong moment — so it is kept
    /// short. Invalidation, not expiry, is what normally makes a change visible.
    /// </summary>
    public TimeSpan EntryLifetime { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Keeps each agency's markup rules in Redis, versioned by a per-agency generation number.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generation number rather than deleting the key.</b> Picture two requests: A is
/// filling the cache and has just read the <i>old</i> rules from PostgreSQL; B commits a rule
/// change and deletes the key. A then writes its old rules back. The change is now hidden until
/// the entry expires — and nothing logged it.
/// </para>
/// <para>
/// With a generation, B increments <c>…:gen</c> instead. A read the generation before loading, so
/// its stale write lands under the <i>old</i> generation's key, which nobody reads any more. Every
/// later request reads the new generation, misses, and loads the new rules. Old keys expire on
/// their own.
/// </para>
/// <para>
/// <b>Failures degrade to the database, never to an error.</b> See <see cref="IMarkupRuleCache"/>.
/// </para>
/// </remarks>
public sealed partial class RedisMarkupRuleCache : IMarkupRuleCache
{
    /// <summary>Bump the version segment whenever the stored JSON shape changes.</summary>
    private const string KeyPrefix = "pricing:markup-rules:v1:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly MarkupRuleCacheOptions _options;
    private readonly ILogger<RedisMarkupRuleCache> _logger;

    public RedisMarkupRuleCache(
        IConnectionMultiplexer redis,
        MarkupRuleCacheOptions options,
        ILogger<RedisMarkupRuleCache> logger)
    {
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    public async Task<MarkupRuleSet> GetOrLoadAsync(
        Guid agencyId,
        Func<CancellationToken, Task<MarkupRuleSet>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);

        var redis = _redis.GetDatabase();
        long generation;

        try
        {
            var stored = await redis.StringGetAsync(GenerationKey(agencyId));
            generation = stored.HasValue ? (long)stored : 0;

            var cached = await redis.StringGetAsync(EntryKey(agencyId, generation));

            if (cached.HasValue && Deserialize(cached!) is { } hit)
            {
                return hit;
            }
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogReadFailed(_logger, agencyId, ex);
            return await load(cancellationToken);
        }

        var loaded = await load(cancellationToken);

        try
        {
            await redis.StringSetAsync(EntryKey(agencyId, generation), Serialize(loaded), _options.EntryLifetime);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogWriteFailed(_logger, agencyId, ex);
        }

        return loaded;
    }

    public async Task InvalidateAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().StringIncrementAsync(GenerationKey(agencyId));
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // The rule change is already committed; failing the request now would tell the agent
            // it did not save when it did. Logged as an error because prices can lag the change
            // until the entry expires.
            LogInvalidationFailed(_logger, agencyId, _options.EntryLifetime, ex);
        }
    }

    /// <summary>The key holding an agency's current generation number.</summary>
    public static string GenerationKey(Guid agencyId) =>
        string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}{agencyId:N}:gen");

    private static string EntryKey(Guid agencyId, long generation) =>
        string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}{agencyId:N}:g{generation}");

    // RedisTimeoutException derives from TimeoutException, not RedisException, so both are named.
    private static bool IsRedisFailure(Exception ex) => ex is RedisException or TimeoutException;

    private static string Serialize(MarkupRuleSet set) => JsonSerializer.Serialize(CachedRuleSet.From(set), Json);

    private static MarkupRuleSet? Deserialize(string json) =>
        JsonSerializer.Deserialize<CachedRuleSet>(json, Json)?.ToRuleSet();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Markup rule cache read failed for agency {AgencyId}; pricing from the database instead.")]
    private static partial void LogReadFailed(ILogger logger, Guid agencyId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Markup rule cache write failed for agency {AgencyId}; the next price will load from the database again.")]
    private static partial void LogWriteFailed(ILogger logger, Guid agencyId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Markup rule cache invalidation failed for agency {AgencyId}. The rule change is saved, but prices "
                  + "may use the previous rules for up to {EntryLifetime}.")]
    private static partial void LogInvalidationFailed(
        ILogger logger, Guid agencyId, TimeSpan entryLifetime, Exception exception);

    /// <summary>
    /// The stored shape: explicit and flat, so the JSON in Redis does not change whenever a domain
    /// type gains a property. Money is plain minor units here, as everywhere outside the domain.
    /// </summary>
    private sealed record CachedRuleSet(Guid AgencyId, Guid? ParentAgencyId, List<CachedRule> Rules)
    {
        public static CachedRuleSet From(MarkupRuleSet set) =>
            new(set.AgencyId, set.ParentAgencyId, set.Rules.Select(CachedRule.From).ToList());

        public MarkupRuleSet ToRuleSet() =>
            new(AgencyId, ParentAgencyId, Rules.Select(rule => rule.ToDefinition()).ToList());
    }

    private sealed record CachedRule(
        Guid Id,
        Guid AgencyId,
        MarkupScope Scope,
        PricedProductType? ProductType,
        Guid? ProductId,
        string? SupplierCode,
        string Currency,
        MarkupCalculationType CalculationType,
        int? PercentBasisPoints,
        long? ValueMinor,
        long? MinMarkupMinor,
        long? MaxMarkupMinor,
        int Priority,
        bool AppliesToSubAgents,
        DateTimeOffset EffectiveFrom,
        DateTimeOffset? EffectiveTo)
    {
        public static CachedRule From(MarkupRuleDefinition rule) =>
            new(
                rule.Id,
                rule.AgencyId,
                rule.Terms.Scope,
                rule.Terms.ProductType,
                rule.Terms.ProductId,
                rule.Terms.SupplierCode,
                rule.Terms.Currency,
                rule.Terms.CalculationType,
                rule.Terms.PercentBasisPoints,
                rule.Terms.ValueMinor?.AmountMinor,
                rule.Terms.MinMarkupMinor?.AmountMinor,
                rule.Terms.MaxMarkupMinor?.AmountMinor,
                rule.Terms.Priority,
                rule.Terms.AppliesToSubAgents,
                rule.Terms.EffectiveFrom,
                rule.Terms.EffectiveTo);

        public MarkupRuleDefinition ToDefinition() =>
            new(Id, AgencyId, new MarkupRuleTerms
            {
                Scope = Scope,
                ProductType = ProductType,
                ProductId = ProductId,
                SupplierCode = SupplierCode,
                Currency = Currency,
                CalculationType = CalculationType,
                PercentBasisPoints = PercentBasisPoints,
                ValueMinor = ValueMinor is { } value ? new Money(value) : null,
                MinMarkupMinor = MinMarkupMinor is { } min ? new Money(min) : null,
                MaxMarkupMinor = MaxMarkupMinor is { } max ? new Money(max) : null,
                Priority = Priority,
                AppliesToSubAgents = AppliesToSubAgents,
                EffectiveFrom = EffectiveFrom,
                EffectiveTo = EffectiveTo,
            });
    }
}

/// <summary>
/// No cache at all: every price reads the rules from the database. Used when no Redis connection
/// string is configured, so a checkout without Redis is slower rather than broken.
/// </summary>
public sealed partial class DatabaseOnlyMarkupRuleCache : IMarkupRuleCache
{
    public DatabaseOnlyMarkupRuleCache(ILogger<DatabaseOnlyMarkupRuleCache> logger) =>
        LogNoCache(logger ?? throw new ArgumentNullException(nameof(logger)));

    public Task<MarkupRuleSet> GetOrLoadAsync(
        Guid agencyId,
        Func<CancellationToken, Task<MarkupRuleSet>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        return load(cancellationToken);
    }

    public Task InvalidateAsync(Guid agencyId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No Redis connection string is configured (ConnectionStrings:Redis). Markup rules will be read "
                  + "from PostgreSQL on every price. Correct, but slower — set it outside local development.")]
    private static partial void LogNoCache(ILogger logger);
}
