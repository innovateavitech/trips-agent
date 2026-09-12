using System.Text;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Api.Platform;

/// <summary>
/// The back-office view of the platform's travel agencies (epic 66).
/// </summary>
/// <remarks>
/// <para>
/// Permissions are layered on purpose, because the four back-office roles are not the same person:
/// reading the directory is <c>agency.view</c>, which everyone in the back office holds; editing is
/// <c>agency.manage</c>; suspending is <c>agency.suspend</c>; terminating and exporting are their
/// own codes again, and only a Super Admin holds either.
/// </para>
/// <para>
/// Every write here demands a reason, and the audit entry is written by the save interceptor
/// rather than by these routes, so it cannot be forgotten.
/// </para>
/// </remarks>
public static class AgencyAdminEndpoints
{
    public static IEndpointRouteBuilder MapAgencyAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/agencies")
            .WithTags("Agency administration")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencyView));

        group.MapGet("/", async (
                string? search,
                string? status,
                string? type,
                string? sort,
                int? page,
                int? pageSize,
                AgencyDirectoryService directory,
                CancellationToken cancellationToken) =>
            {
                // A filter nobody recognises is a 400, not "everything": silently widening a
                // filter is how somebody screenshots the wrong list and acts on it.
                if (!TryParse<AgencyStatus>(status, out var parsedStatus))
                {
                    return Invalid("status", status, Enum.GetNames<AgencyStatus>());
                }

                if (!TryParse<AgencyType>(type, out var parsedType))
                {
                    return Invalid("type", type, Enum.GetNames<AgencyType>());
                }

                if (!TryParse<AgencySort>(sort, out var parsedSort))
                {
                    return Invalid("sort", sort, Enum.GetNames<AgencySort>());
                }

                var query = new AgencyDirectoryQuery(
                    search,
                    parsedStatus,
                    parsedType,
                    parsedSort ?? AgencySort.Newest,
                    page ?? 1,
                    pageSize ?? 25);

                return Results.Ok(await directory.SearchAsync(query, cancellationToken));
            })
            .WithName("AgencyDirectory")
            .Produces<AgencyDirectoryResponse>()
            .ProducesValidationProblem();

        group.MapGet("/{agencyId:guid}", async (
                Guid agencyId,
                AgencyDirectoryService directory,
                CancellationToken cancellationToken) =>
            {
                var profile = await directory.ProfileAsync(agencyId, cancellationToken);

                return profile is null ? Results.NotFound() : Results.Ok(profile);
            })
            .WithName("AgencyProfile")
            .Produces<AgencyProfileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{agencyId:guid}", async (
                Guid agencyId,
                UpdateAgencyRequest request,
                AgencyLifecycleService lifecycle,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                return Render(await lifecycle.UpdateAsync(agencyId, request, cancellationToken));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencyManage))
            .WithName("UpdateAgency")
            .Produces<AgencyStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{agencyId:guid}/verify", async (
                Guid agencyId,
                AgencyStatusChangeRequest request,
                AgencyLifecycleService lifecycle,
                CancellationToken cancellationToken) =>
                Render(await lifecycle.VerifyAsync(agencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.KybReview))
            .WithName("VerifyAgency")
            .Produces<AgencyStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{agencyId:guid}/suspend", async (
                Guid agencyId,
                AgencyStatusChangeRequest request,
                AgencyLifecycleService lifecycle,
                CancellationToken cancellationToken) =>
                Render(await lifecycle.SuspendAsync(agencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencySuspend))
            .WithName("SuspendAgency")
            .Produces<AgencyStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{agencyId:guid}/reinstate", async (
                Guid agencyId,
                AgencyStatusChangeRequest request,
                AgencyLifecycleService lifecycle,
                CancellationToken cancellationToken) =>
                Render(await lifecycle.ReinstateAsync(agencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencySuspend))
            .WithName("ReinstateAgency")
            .Produces<AgencyStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{agencyId:guid}/terminate", async (
                Guid agencyId,
                AgencyStatusChangeRequest request,
                AgencyLifecycleService lifecycle,
                CancellationToken cancellationToken) =>
                Render(await lifecycle.TerminateAsync(agencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencyTerminate))
            .WithName("TerminateAgency")
            .Produces<AgencyStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{agencyId:guid}/export", async (
                Guid agencyId,
                AgencyExportService exports,
                Application.Persistence.IAppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var export = await exports.BuildAsync(agencyId, cancellationToken);

                if (export is null)
                {
                    return Results.NotFound();
                }

                // Saved so the audit entry the service staged actually lands: reading is not a
                // change, so nothing else in this request would call SaveChanges.
                await db.SaveChangesAsync(cancellationToken);

                return Results.File(
                    Encoding.UTF8.GetBytes(export.Json),
                    "application/json",
                    export.FileName);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencyExport))
            .WithName("ExportAgency")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>Turns one outcome into the response that says the same thing over HTTP.</summary>
    private static IResult Render(AgencyActionOutcome outcome) => outcome switch
    {
        AgencyActionOutcome.Done done => Results.Ok(done.Status),

        AgencyActionOutcome.NotFound => Results.NotFound(),

        // A validation failure rather than a silent default: the reason is the whole point of
        // these routes, and it is written into a record somebody will read years from now.
        AgencyActionOutcome.ReasonRequired => Results.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] =
                [
                    $"Say why, in at least {AgencyLifecycleService.MinReasonLength} characters. "
                    + "It is recorded against your name in the audit log.",
                ],
            }),

        AgencyActionOutcome.NotAllowed notAllowed => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That cannot be done from where this agency is now.",
            detail: notAllowed.Detail),

        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };

    /// <summary>Parses an optional enum from the query string. Blank means "no filter".</summary>
    private static bool TryParse<T>(string? value, out T? parsed)
        where T : struct, Enum
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<T>(value, ignoreCase: true, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static IResult Invalid(string field, string? value, string[] allowed) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = [$"'{value}' is not one of: {string.Join(", ", allowed)}."],
        });
}
