using TripsAgent.Api.Authorization;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Platform;

/// <summary>
/// The audit viewer: who did what to whom, and why (FRD §2.15 RS-5).
/// </summary>
/// <remarks>
/// Read-only, and there is no write route because there is no write path — the table refuses
/// updates and deletes outright. <c>audit.view</c> is held by Super Admin, Operations and
/// Finance; Support does not have it, because reading colleagues' actions is oversight work
/// rather than support work.
/// </remarks>
public static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/audit-logs")
            .WithTags("Audit log")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AuditView));

        group.MapGet("/", async (
                Guid? agencyId,
                Guid? actorUserId,
                string? action,
                string? entityType,
                string? entityId,
                DateTimeOffset? from,
                DateTimeOffset? to,
                int? page,
                int? pageSize,
                AuditLogQueryService audit,
                CancellationToken cancellationToken) =>
            {
                // Converted to UTC here rather than anywhere deeper: Npgsql refuses a
                // DateTimeOffset that is not UTC, and the query string can carry any offset the
                // browser felt like sending.
                var query = new AuditLogQuery(
                    agencyId,
                    actorUserId,
                    action,
                    entityType,
                    entityId,
                    from?.ToUniversalTime(),
                    to?.ToUniversalTime(),
                    page ?? 1,
                    pageSize ?? 50);

                return Results.Ok(await audit.SearchAsync(query, cancellationToken));
            })
            .WithName("AuditLogSearch")
            .Produces<AuditLogPageResponse>();

        group.MapGet("/actions", async (AuditLogQueryService audit, CancellationToken cancellationToken) =>
                Results.Ok(await audit.ActionsAsync(cancellationToken)))
            .WithName("AuditLogActions")
            .Produces<IReadOnlyList<string>>();

        return app;
    }
}
