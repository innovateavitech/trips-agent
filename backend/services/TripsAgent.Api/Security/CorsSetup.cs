using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripsAgent.Application.Storefront;

namespace TripsAgent.Api.Security;

/// <summary>
/// The consoles' own addresses, the only origins trusted with a signed-in session (issue 107).
/// </summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins, such as <c>https://app.example.com</c>: scheme, host and port, no path. Empty is
    /// fine when the consoles reach the API through their own host, which is how development works.
    /// </summary>
    public IReadOnlyList<string> ConsoleOrigins { get; init; } = [];
}

/// <summary>Wires cross-origin access: see <see cref="TripsCorsPolicyProvider"/>.</summary>
public static class CorsSetup
{
    public static IServiceCollection AddTripsCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var origins = configuration.GetSection(CorsSettings.SectionName).GetSection(nameof(CorsSettings.ConsoleOrigins))
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormaliseConsoleOrigin(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        services.AddSingleton(new CorsSettings { ConsoleOrigins = origins });
        services.AddCors();

        // Scoped, replacing the default: deciding about a storefront's origin reads the domains table
        // through the request's own database context. CorsMiddleware resolves the provider per request.
        services.Replace(ServiceDescriptor.Scoped<ICorsPolicyProvider, TripsCorsPolicyProvider>());

        return services;
    }

    /// <summary>
    /// Refuses a wildcard or a malformed origin at startup. <c>*</c> with credentials would hand any
    /// website on the internet a signed-in agent's session.
    /// </summary>
    public static string NormaliseConsoleOrigin(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim().TrimEnd('/');

        if (trimmed.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidOperationException(
                $"{CorsSettings.SectionName}:{nameof(CorsSettings.ConsoleOrigins)} must list exact origins such as "
                + $"https://app.example.com, with no wildcard and no path. '{value}' is not one.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}

/// <summary>
/// Decides, per request, which origin may read an API response from a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>A console origin</b>, listed in configuration, gets credentials: the refresh cookie and the
/// Authorization header. The list is short, fixed and ours.
/// </para>
/// <para>
/// <b>An agency's storefront</b> gets the anonymous traveller routes, <c>/api/v1/public/</c>, and
/// never credentials. Its origin is data, not configuration: it must be a hostname in the domains
/// table that is verified and not held for review, written the way the site's address is written.
/// That answer comes from the same host cache the storefront itself routes by, so it is cached, and
/// the cache is dropped whenever a domain is verified, removed or reviewed.
/// </para>
/// <para>
/// A storefront never gets credentials because a storefront page is on a site the agency controls
/// the content of. Were it trusted with the cookie, a subdomain storefront — same site as the API —
/// could read a signed-in agent's access token straight out of <c>/api/v1/auth/refresh</c>.
/// </para>
/// <para>
/// <b>Anything else</b> gets no CORS headers at all, and there is never a wildcard.
/// </para>
/// </remarks>
public sealed class TripsCorsPolicyProvider : ICorsPolicyProvider
{
    /// <summary>The traveller-facing routes a storefront origin may call.</summary>
    public const string PublicPathPrefix = "/api/v1/public/";

    /// <summary>How long a browser may reuse a preflight answer.</summary>
    private static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(10);

    private readonly CorsSettings _settings;
    private readonly IStorefrontDirectory _storefronts;
    private readonly StorefrontOptions _storefrontOptions;

    public TripsCorsPolicyProvider(
        CorsSettings settings,
        IStorefrontDirectory storefronts,
        StorefrontOptions storefrontOptions)
    {
        _settings = settings;
        _storefronts = storefronts;
        _storefrontOptions = storefrontOptions;
    }

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        ArgumentNullException.ThrowIfNull(context);

        var origin = context.Request.Headers.Origin.ToString();

        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (_settings.ConsoleOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return new CorsPolicyBuilder()
                .WithOrigins(origin)
                .AllowCredentials()
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders("Content-Disposition", "X-Correlation-Id", "Retry-After")
                .SetPreflightMaxAge(PreflightMaxAge)
                .Build();
        }

        if (!context.Request.Path.StartsWithSegments(PublicPathPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var agency = await _storefronts.FindAgencyAsync(uri.Host, context.RequestAborted);

        // The origin must be exactly how the site's address is written — https in production — so an
        // http page on the same name, or another port, does not qualify.
        if (agency is null
            || !string.Equals(origin.TrimEnd('/'), _storefrontOptions.SiteUrlFor(uri.Host.ToLowerInvariant()), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new CorsPolicyBuilder()
            .WithOrigins(origin)
            .DisallowCredentials()
            .AllowAnyHeader()
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete)
            .SetPreflightMaxAge(PreflightMaxAge)
            .Build();
    }
}
