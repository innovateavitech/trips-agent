using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Infrastructure.Pricing;

namespace TripsAgent.Infrastructure.RateLimiting;

/// <summary>
/// Registers the shared request-count store, and reads the rate limit settings.
/// </summary>
/// <remarks>
/// The limiter itself — the ASP.NET Core middleware — lives in the API, the only process that serves
/// requests. What is here is what it needs from outside: somewhere to keep counts, and the numbers.
/// </remarks>
public static class RateLimitingRegistration
{
    /// <summary>The configuration section the settings are read from.</summary>
    public const string SectionName = "RateLimiting";

    private static readonly TimeSpan ShortestWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// Registers <see cref="IRateLimitStore"/> on the process's Redis connection, when there is one.
    /// </summary>
    /// <remarks>
    /// With no Redis configured nothing is registered, deliberately — there is no in-memory stand-in.
    /// A per-process count looks like it works on one machine and quietly multiplies every limit by
    /// the number of instances in production. The API refuses to start with rate limiting switched on
    /// and no store, so the gap is loud; see <c>RateLimitingSetup</c>.
    /// </remarks>
    public static IServiceCollection AddRateLimitStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(PricingRegistration.RedisConnectionName)))
        {
            return services;
        }

        // The multiplexer AddPricingCache registered under exactly the same condition.
        services.AddSingleton<IRateLimitStore>(provider => new RedisRateLimitStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<RedisRateLimitStore>>()));

        return services;
    }

    /// <summary>
    /// Reads <c>RateLimiting:Enabled</c> and <c>RateLimiting:Policies:{name}:PermitLimit|Window</c>.
    /// Anything unset keeps its default from <see cref="RateLimitSettings.Defaults"/>.
    /// </summary>
    /// <remarks>
    /// A bad value stops startup rather than falling back to the default, and so does a policy name
    /// nobody recognises: a typo in production config — <c>Logn</c> for <c>Login</c> — would otherwise
    /// leave the real policy on its default while everyone believed it had been changed.
    /// </remarks>
    public static RateLimitSettings ReadSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var policies = section.GetSection("Policies");

        var unknown = policies.GetChildren()
            .Select(child => child.Key)
            .Where(name => !RateLimitPolicyNames.All.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unknown rate limit polic{(unknown.Count == 1 ? "y" : "ies")} under {SectionName}:Policies: "
                + $"{string.Join(", ", unknown)}. The known ones are {string.Join(", ", RateLimitPolicyNames.All)}.");
        }

        var rules = new Dictionary<string, RateLimitRule>(StringComparer.Ordinal);

        foreach (var name in RateLimitPolicyNames.All)
        {
            var fallback = RateLimitSettings.Defaults[name];
            var policy = policies.GetSection(name);

            rules[name] = new RateLimitRule(
                ReadPermitLimit(policy["PermitLimit"], fallback.PermitLimit, name),
                ReadWindow(policy["Window"], fallback.Window, name));
        }

        return new RateLimitSettings(ReadEnabled(section["Enabled"]), rules);
    }

    private static bool ReadEnabled(string? value)
    {
        // Blank means "the default", which is on. Only an explicit false switches it off.
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return bool.TryParse(value.Trim(), out var enabled)
            ? enabled
            : throw new InvalidOperationException(
                $"{SectionName}__Enabled must be true or false. It was '{value}'.");
    }

    private static int ReadPermitLimit(string? value, int fallback, string policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}__Policies__{policy}__PermitLimit must be a whole number, at least 1. It was '{value}'.");
        }

        return limit;
    }

    private static TimeSpan ReadWindow(string? value, TimeSpan fallback, string policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var window)
            || window < ShortestWindow
            || window > LongestWindow)
        {
            throw new InvalidOperationException(
                $"{SectionName}__Policies__{policy}__Window must be a duration between one second and one "
                + $"day, written hh:mm:ss — 00:05:00 is five minutes. It was '{value}'.");
        }

        return window;
    }
}
