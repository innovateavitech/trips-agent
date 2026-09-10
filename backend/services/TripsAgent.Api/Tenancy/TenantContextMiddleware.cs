using System.Security.Claims;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// Reads the caller's identity off their token and populates <see cref="TenantContext"/> for the
/// rest of the request.
/// </summary>
/// <remarks>
/// <para>
/// This runs once, early, and everything downstream — every query filter, every insert — depends
/// on it. It deliberately does nothing for an unauthenticated request: an unresolved tenant makes
/// tenant-scoped queries return nothing, which is the safe outcome. Storefront traffic is
/// anonymous and resolves its agency from the host name instead, which arrives with the
/// storefront work.
/// </para>
/// <para>
/// The claims it reads are the ones JWT issuance (#16) writes. Until that lands nothing populates
/// them in production, and the middleware is a no-op — which is why the tests here drive it with
/// a hand-built <see cref="ClaimsPrincipal"/> rather than waiting.
/// </para>
/// </remarks>
public sealed partial class TenantContextMiddleware
{
    /// <summary>Claim holding the agency the caller is acting as.</summary>
    public const string AgencyIdClaim = "agency_id";

    /// <summary>Claim holding the principal at the top of that agency's tree.</summary>
    public const string RootAgencyIdClaim = "root_agency_id";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var user = context.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            var userId = ReadGuid(user, ClaimTypes.NameIdentifier) ?? ReadGuid(user, "sub");
            var agencyId = ReadGuid(user, AgencyIdClaim);

            if (agencyId.HasValue)
            {
                tenantContext.SetTenant(
                    agencyId.Value,
                    ReadGuid(user, RootAgencyIdClaim),
                    userId);
            }
            else if (userId.HasValue)
            {
                // Platform staff have no agency of their own. They still get a user id, so an
                // audited cross-tenant read can record who performed it.
                tenantContext.SetPlatformUser(userId.Value);
            }
            else
            {
                LogUnusableToken(_logger, context.Request.Path);
            }
        }

        await _next(context);
    }

    private static Guid? ReadGuid(ClaimsPrincipal user, string claimType) =>
        Guid.TryParse(user.FindFirstValue(claimType), out var value) ? value : null;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Authenticated request to {Path} carried neither an agency nor a user claim; "
                  + "the tenant is unresolved and tenant-scoped queries will return nothing.")]
    private static partial void LogUnusableToken(ILogger logger, string path);
}

/// <summary>Registers <see cref="TenantContextMiddleware"/> in the pipeline.</summary>
public static class TenantContextMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant resolution. Must run <b>after</b> authentication — there is no identity to read
    /// before that — and before anything that touches the database.
    /// </summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
