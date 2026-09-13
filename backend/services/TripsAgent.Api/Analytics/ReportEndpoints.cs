using System.Security.Claims;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Analytics;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Analytics;
using TripsAgent.Domain.Analytics;

namespace TripsAgent.Api.Analytics;

/// <summary>
/// Running reports, listing what has been run, and downloading a finished file.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of endpoints for both scopes.</b> An agency report and a platform report are the same
/// operation over a different set of rows, and which permission is needed is a property of the
/// report — <c>report_definitions.required_permission</c> — not of the route. So the group requires
/// only an authenticated caller, and <see cref="ReportService"/> refuses a definition the caller's
/// token does not carry the permission for. A route per scope would have meant the route and the
/// definition could disagree about who may run what, and only one of them is the source of truth.
/// </para>
/// <para>
/// <b>Sync or async is decided, not chosen.</b> A report inside ninety days and inside one agency
/// comes back as the file. Anything longer, or crossing agencies, answers 202 with the recorded run;
/// the requester is emailed when it is ready and collects it from the list.
/// </para>
/// </remarks>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/reports")
            .WithTags("Reports")
            .RequireAuthorization();

        group.MapGet("/definitions", async (
                ClaimsPrincipal user,
                ReportService reports,
                CancellationToken cancellationToken) =>
                Results.Ok(await reports.DefinitionsAsync(PermissionsOf(user), cancellationToken)))
            .WithName("ListReportDefinitions")
            .Produces<List<ReportDefinitionResponse>>();

        group.MapPost("/", async (
                RunReportRequest request,
                ClaimsPrincipal user,
                ReportService reports,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var result = await reports.RequestAsync(
                    request.DefinitionCode,
                    request.From,
                    request.To,
                    PermissionsOf(user),
                    Pricing.PricingEndpoints.CanViewMargin(user),
                    cancellationToken);

                return result.Outcome switch
                {
                    ReportRequestOutcome.UnknownDefinition => Results.NotFound(new { message = result.Message }),
                    ReportRequestOutcome.Forbidden => Results.Forbid(),
                    ReportRequestOutcome.InvalidWindow or ReportRequestOutcome.WrongScope =>
                        Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["definitionCode"] = [result.Message ?? "This report cannot be run as asked."],
                        }),

                    // Queued: the caller gets the run to watch, not a file.
                    _ when result.Content is null =>
                        Results.Accepted($"/api/v1/reports/jobs/{result.Job!.Id}", ReportService.ToResponse(result.Job!)),

                    // Small enough to answer here. The bytes come straight back, and the export is
                    // already logged.
                    _ => Results.File(
                        result.Content.Content,
                        ReportContent.ContentType,
                        ReportService.FileNameFor(result.Job!)),
                };
            })
            .WithName("RunReport")
            .Produces<ReportJobResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK, contentType: ReportContent.ContentType)
            .ProducesValidationProblem();

        group.MapGet("/jobs", async (
                int? limit,
                ReportService reports,
                CancellationToken cancellationToken) =>
                Results.Ok(await reports.JobsAsync(limit ?? 25, cancellationToken)))
            .WithName("ListReportJobs")
            .Produces<List<ReportJobResponse>>();

        group.MapGet("/jobs/{jobId:guid}", async (
                Guid jobId,
                ReportService reports,
                CancellationToken cancellationToken) =>
            {
                var job = await reports.JobAsync(jobId, cancellationToken);
                return job is null ? Results.NotFound() : Results.Ok(job);
            })
            .WithName("GetReportJob")
            .Produces<ReportJobResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/jobs/{jobId:guid}/download", async (
                Guid jobId,
                ReportService reports,
                CancellationToken cancellationToken) =>
            {
                // Not found rather than forbidden for a job belonging to another agency: the query
                // filter already made it invisible, and saying "that exists but is not yours" would
                // confirm another agency ran a report.
                var download = await reports.DownloadAsync(jobId, cancellationToken);

                return download is null
                    ? Results.NotFound()
                    : Results.File(download.Content, download.ContentType, download.FileName);
            })
            .WithName("DownloadReport")
            .Produces(StatusCodes.Status200OK, contentType: ReportContent.ContentType)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>Every permission code on the caller's token.</summary>
    private static HashSet<string> PermissionsOf(ClaimsPrincipal user) =>
        user.FindAll(TripsClaimTypes.Permission).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
}
