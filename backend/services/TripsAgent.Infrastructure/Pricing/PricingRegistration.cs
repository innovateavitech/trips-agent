using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.Pricing;

namespace TripsAgent.Infrastructure.Pricing;

/// <summary>Wires the markup rule cache: Redis when configured, the database alone when not.</summary>
public static class PricingRegistration
{
    /// <summary>The configuration key holding the Redis connection string.</summary>
    public const string RedisConnectionName = "Redis";

    public static IServiceCollection AddPricingCache(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);
        services.AddSingleton(options);

        var connectionString = configuration.GetConnectionString(RedisConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IMarkupRuleCache, DatabaseOnlyMarkupRuleCache>();
            return services;
        }

        // One multiplexer per process: it is thread-safe and designed to be shared, and a new one
        // per request would open a new TCP connection per request.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redis = ConfigurationOptions.Parse(connectionString);

            // Start even if Redis is down, and keep retrying in the background. The cache is an
            // optimisation: an API that refuses to boot without it would turn a Redis outage into
            // a full outage.
            redis.AbortOnConnectFail = false;

            return ConnectionMultiplexer.Connect(redis);
        });

        services.AddSingleton<IMarkupRuleCache>(sp => new RedisMarkupRuleCache(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            options,
            sp.GetRequiredService<ILogger<RedisMarkupRuleCache>>()));

        return services;
    }

    private static MarkupRuleCacheOptions ReadOptions(IConfiguration configuration)
    {
        var minutes = configuration[$"{MarkupRuleCacheOptions.SectionName}:RuleCacheMinutes"];

        if (string.IsNullOrWhiteSpace(minutes))
        {
            return new MarkupRuleCacheOptions();
        }

        if (!int.TryParse(minutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new InvalidOperationException(
                $"{MarkupRuleCacheOptions.SectionName}:RuleCacheMinutes must be a whole number of minutes, at least 1. "
                + $"It was '{minutes}'.");
        }

        return new MarkupRuleCacheOptions { EntryLifetime = TimeSpan.FromMinutes(value) };
    }
}
