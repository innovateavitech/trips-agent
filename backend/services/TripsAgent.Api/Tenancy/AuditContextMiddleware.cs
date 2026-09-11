using System.Diagnostics;
using TripsAgent.Application.Identity;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// Tells the audit log who is acting.
/// </summary>
/// <remarks>
/// <para>
/// The audit interceptor records what changed and from what to what; without this it records the
/// actor as null, so every admin decision would be attributed to nobody — which makes the log
/// useless for the question it exists to answer.
/// </para>
/// <para>
/// Runs after <see cref="TenantContextMiddleware"/>, so the agency it reports is the one that
/// request resolved rather than a second reading of the same claims.
/// </para>
/// </remarks>
public sealed class AuditContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuditContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        AuditContext auditContext,
        Application.Tenancy.ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auditContext);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (context.User?.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(TripsClaimTypes.Subject)?.Value, out var userId))
        {
            auditContext.ActorUserId = userId;
            auditContext.ActorType = AuditActorType.User;
        }

        auditContext.AgencyId = tenantContext.AgencyId;
        auditContext.ActorIpAddress = context.Connection.RemoteIpAddress?.ToString();

        // The same correlation id the response header carries, so a log line and an audit row
        // can be tied to each other.
        auditContext.CorrelationId =
            context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Activity.Current?.Id
            ?? context.TraceIdentifier;

        await _next(context);
    }
}

/// <summary>Registers <see cref="AuditContextMiddleware"/>.</summary>
public static class AuditContextMiddlewareExtensions
{
    /// <summary>Must run after authentication and after tenant resolution.</summary>
    public static IApplicationBuilder UseAuditContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuditContextMiddleware>();
    }
}
