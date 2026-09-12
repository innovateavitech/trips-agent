using TripsAgent.Api.Authorization;
using TripsAgent.Application.Documents;
using TripsAgent.Contracts.Documents;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Documents;

/// <summary>
/// A booking's invoices and vouchers (#46): list them, reissue one, and download a PDF — by the
/// agent through a signed link, or by the customer through their own.
/// </summary>
/// <remarks>
/// <para>
/// Listing and reissuing are the agent's, behind their token and a permission. The two downloads
/// are anonymous on purpose: a PDF opens in a new tab, which sends no Authorization header, so the
/// signed link is the credential — the same arrangement as a KYB document or an object store's
/// presigned URL.
/// </para>
/// <para>
/// The bytes pass through the API rather than a presigned storage URL so that every download is
/// checked against the checksum recorded when the document was issued. That check is what "a
/// reprint is byte-identical to the original" rests on; a PDF is small enough for it to cost nothing.
/// </para>
/// </remarks>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/documents").WithTags("Documents");

        group.MapGet("/", async (
                string? orderReference,
                BookingDocumentsHandler handler,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(orderReference))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["orderReference"] = ["Say which booking: its reference, e.g. ORD-2026-000142."],
                    });
                }

                var documents = await handler.ListForOrderAsync(orderReference, cancellationToken);

                // Not found and not yours look the same, on purpose: the difference is another agency's business.
                return documents is null
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "We could not find that booking.",
                        detail: "It may belong to another agency, or the reference may be mistyped.")
                    : Results.Ok(documents.Select(ToResponse).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch))
            .WithName("ListBookingDocuments")
            .Produces<List<BookingDocumentResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{documentId:guid}/reissue", async (
                Guid documentId,
                BookingDocumentsHandler handler,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.ReissueAsync(documentId, cancellationToken);

                return outcome switch
                {
                    // 202: numbered and queued. The PDF follows, and the customer is emailed a copy.
                    ReissueDocumentOutcome.Reissued reissued => Results.Accepted(
                        value: ToResponse(reissued.Document)),

                    ReissueDocumentOutcome.NotFound => Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "We could not find that document."),

                    ReissueDocumentOutcome.NotReady => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "That document is still being prepared.",
                        detail: "It can be reissued once its PDF is ready."),

                    ReissueDocumentOutcome.AlreadySuperseded superseded => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: $"That document has already been replaced by {superseded.ByDocumentNumber}.",
                        detail: "Reissue the newest one instead."),

                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingIssue))
            .WithName("ReissueBookingDocument")
            .Produces<BookingDocumentResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The agent's download, reached through the signed link the list hands out.
        group.MapGet("/{documentId:guid}/pdf", async (
                Guid documentId,
                long expires,
                string? signature,
                DocumentLinks links,
                BookingDocumentsHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                if (!links.IsValidDownload(documentId, expires, signature))
                {
                    return Refused();
                }

                return ToFile(await handler.OpenAsync(documentId, forCustomer: false, cancellationToken), http);
            })
            .AllowAnonymous()
            .WithName("DownloadBookingDocument")
            .ExcludeFromDescription();

        // The customer's own link. Permanent, and refuses a document their agent has since replaced.
        app.MapGet("/api/v1/public/documents/{documentId:guid}/{token}", async (
                Guid documentId,
                string token,
                DocumentLinks links,
                BookingDocumentsHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                if (!links.IsValidPublic(documentId, token))
                {
                    return Refused();
                }

                return ToFile(await handler.OpenAsync(documentId, forCustomer: true, cancellationToken), http);
            })
            .AllowAnonymous()
            .WithName("DownloadCustomerDocument")
            .ExcludeFromDescription();

        return app;
    }

    private static IResult ToFile(DocumentFileOutcome outcome, HttpContext http)
    {
        switch (outcome)
        {
            case DocumentFileOutcome.File file:
                // A PDF, and nothing a browser should second-guess or keep.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(file.Content, "application/pdf", file.FileName);

            case DocumentFileOutcome.Superseded superseded:
                // Words a traveller may read: the agency's, never ours.
                return Results.Problem(
                    statusCode: StatusCodes.Status410Gone,
                    title: "This document has been replaced.",
                    detail: $"Please use {superseded.ByDocumentNumber} instead, or ask your travel agent for it.");

            case DocumentFileOutcome.Corrupted:
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "This document cannot be shown right now.");

            default:
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "We could not find that document.");
        }
    }

    /// <summary>One answer for a bad signature and an expired link: which it was tells a guesser what to change.</summary>
    private static IResult Refused() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "This link is not valid, or it has expired.");

    internal static BookingDocumentResponse ToResponse(DocumentView view) => new(
        view.Document.Id,
        view.Document.DocumentType.ToString(),
        view.Document.DocumentNumber,
        view.Document.IssueNumber,
        view.Document.Status.ToString(),
        view.ProductType?.ToString(),
        view.Document.IssuedAt,
        view.SupersedesDocumentNumber,
        view.SupersededByDocumentId,
        view.SupersededByDocumentNumber,
        view.Document.IsReady ? view.Document.FileName : null,
        view.Document.SizeBytes,
        view.Document.Checksum,
        view.Download?.Path,
        view.Download?.ExpiresAt,
        view.Email is { } email ? new BookingDocumentEmailResponse(email.Recipient, email.Status, email.SentAt) : null);
}
