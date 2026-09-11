using Microsoft.Net.Http.Headers;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy.Kyb;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Tenancy;

/// <summary>The Trips back-office side of KYB: the queue, the documents, the decision.</summary>
public static class KybReviewEndpoints
{
    public static IEndpointRouteBuilder MapKybReviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/kyb")
            .WithTags("KYB review")

            // Every route below reads or decides another agency's data, so the permission is
            // required rather than merely authentication. Only Trips staff hold kyb.review.
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.KybReview));

        group.MapGet("/submissions", async (KybReviewHandler handler, CancellationToken cancellationToken) =>
                Results.Ok(await handler.QueueAsync(cancellationToken)))
            .WithName("KybReviewQueue")
            .Produces<IReadOnlyList<KybQueueItemResponse>>();

        group.MapGet("/submissions/{submissionId:guid}", async (
                Guid submissionId,
                KybReviewHandler handler,
                CancellationToken cancellationToken) =>
            {
                var detail = await handler.DetailAsync(submissionId, cancellationToken);

                return detail is null ? Results.NotFound() : Results.Ok(detail);
            })
            .WithName("KybReviewDetail")
            .Produces<KybReviewDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/submissions/{submissionId:guid}/approve", async (
                Guid submissionId,
                KybReviewHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var reviewer = ReviewerId(http);

                var outcome = await handler.ApproveAsync(submissionId, reviewer, cancellationToken);

                return outcome switch
                {
                    KybDecisionOutcome.Decided => Results.Ok(new { message = "Agency verified." }),

                    _ => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "That submission is not awaiting a decision.",
                        detail: "It may already have been decided by someone else."),
                };
            })
            .WithName("ApproveKyb")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/submissions/{submissionId:guid}/reject", async (
                Guid submissionId,
                RejectKybRequest request,
                KybReviewHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.RejectAsync(
                    submissionId, ReviewerId(http), request?.Reason ?? string.Empty, cancellationToken);

                return outcome switch
                {
                    KybDecisionOutcome.Decided => Results.Ok(new { message = "Agency notified." }),

                    // The reason is what the agency is shown. Refusing without one is the whole
                    // point of the rule, so it is a validation failure and not a silent default.
                    KybDecisionOutcome.ReasonRequired => Results.ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["reason"] = ["Say what the agency needs to fix. They see this text."],
                        }),

                    _ => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "That submission is not awaiting a decision."),
                };
            })
            .WithName("RejectKyb")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Deliberately outside the group: a reviewer opens this in a new tab, and a new tab sends
        // no Authorization header. The signed, expiring link *is* the credential — which is why
        // it is scoped to one document id and lasts minutes.
        app.MapGet("/api/v1/admin/kyb/documents/{documentId:guid}", async (
                Guid documentId,
                long expires,
                string signature,
                KybDocumentLink links,
                KybReviewHandler handler,
                IBlobStorage storage,
                CancellationToken cancellationToken) =>
            {
                if (!links.IsValid(documentId, expires, signature))
                {
                    // One answer for a bad signature and an expired link: which it was tells an
                    // attacker whether the document id is real.
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "This link is not valid, or it has expired.",
                        detail: "Reopen the submission to get a fresh link.");
                }

                var document = await handler.FindDocumentAsync(documentId, cancellationToken);

                if (document is null)
                {
                    return Results.NotFound();
                }

                var content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);

                // inline, so a PDF opens in the browser rather than downloading; the filename is
                // already stripped to its own name when the document is stored.
                return Results.File(
                    content,
                    document.ContentType,
                    fileDownloadName: null,
                    enableRangeProcessing: true);
            })
            .WithName("ViewKybDocument")
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>The acting reviewer, from the token the policy already validated.</summary>
    private static Guid ReviewerId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(TripsClaimTypes.Subject)?.Value, out var id)
            ? id
            : throw new InvalidOperationException(
                "An authorised request reached a review endpoint with no subject claim.");
}
