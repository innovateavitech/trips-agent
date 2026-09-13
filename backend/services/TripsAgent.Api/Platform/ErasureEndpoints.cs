using TripsAgent.Api.Authorization;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Platform;

/// <summary>
/// Erasing one person's details on request (NDPA, issue 106).
/// </summary>
/// <remarks>
/// <para>
/// Behind <c>platform.erasure.execute</c>, which only a Super Admin holds. The whole group, including
/// the preview: looking a person up by email across every agency is itself a read no support account
/// has any business making.
/// </para>
/// <para>
/// Two steps on purpose. The preview says what would change and what stands in the way; the erasure
/// itself then takes a customer id and a stated reason, so nobody erases the wrong person by typing
/// an address slightly wrong, and nobody erases anyone without saying why. See
/// <c>docs/adr/0009-ndpa-erasure-as-anonymisation.md</c>.
/// </para>
/// </remarks>
public static class ErasureEndpoints
{
    public static IEndpointRouteBuilder MapErasureEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/erasure-requests")
            .WithTags("Data erasure")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.ErasureExecute));

        group.MapPost("/preview", async (
                ErasurePreviewRequest request,
                CustomerErasureService erasure,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.AgencyId == Guid.Empty || string.IsNullOrWhiteSpace(request.Email))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["email"] = ["Give the agency and the email address the person gave them."],
                    });
                }

                var preview = await erasure.PreviewAsync(request.AgencyId, request.Email, cancellationToken);

                return preview is null
                    ? Results.NotFound()
                    : Results.Ok(new ErasurePreviewResponse(
                        preview.CustomerId,
                        preview.Name,
                        preview.Email,
                        preview.Orders,
                        preview.Travellers,
                        preview.TravelDocuments,
                        preview.Notifications,
                        preview.EvidenceFiles,
                        preview.Blockers));
            })
            .WithName("PreviewErasure")
            .Produces<ErasurePreviewResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                ErasureRequestCommand command,
                CustomerErasureService erasure,
                CancellationToken cancellationToken) =>
            {
                if (command is null
                    || command.AgencyId == Guid.Empty
                    || command.CustomerId == Guid.Empty
                    || (command.Reason ?? string.Empty).Trim().Length < Domain.Platform.ErasureRequest.MinimumReasonLength)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["reason"] =
                        [
                            "Give the agency, the customer from the preview, and a reason of at least "
                            + $"{Domain.Platform.ErasureRequest.MinimumReasonLength} characters. The reason is the only "
                            + "record of why somebody's details were destroyed.",
                        ],
                    });
                }

                try
                {
                    var outcome = await erasure.EraseAsync(
                        command.AgencyId, command.CustomerId, command.Reason ?? string.Empty, cancellationToken);

                    var response = new ErasureResultResponse(
                        outcome.RequestId, outcome.Completed, outcome.Changed, outcome.Blockers);

                    // A refusal is a 409, not a 400: the request was well formed and was recorded; it is
                    // the state of the person's bookings that stops it, and that changes on its own.
                    return outcome.Completed ? Results.Ok(response) : Results.Conflict(response);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                }
            })
            .WithName("EraseCustomer")
            .Produces<ErasureResultResponse>()
            .Produces<ErasureResultResponse>(StatusCodes.Status409Conflict)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", async (int? limit, CustomerErasureService erasure, CancellationToken cancellationToken) =>
            {
                var requests = await erasure.RecentAsync(limit ?? 50, cancellationToken);

                return Results.Ok(requests
                    .Select(request => new ErasureRequestResponse(
                        request.Id,
                        request.AgencyId,
                        request.CustomerId,
                        request.Status.ToString(),
                        request.Reason,
                        request.RequestedByUserId,
                        request.RequestedAt,
                        request.CompletedAt,
                        request.Outcome,
                        request.RefusalReason))
                    .ToList());
            })
            .WithName("ErasureRequests")
            .Produces<IReadOnlyList<ErasureRequestResponse>>();

        return app;
    }
}
