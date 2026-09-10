using TripsAgent.Application.Tenancy.Kyb;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Api.Tenancy;

/// <summary>The KYB step of onboarding: upload documents, submit them, see where they are.</summary>
public static class KybEndpoints
{
    public static IEndpointRouteBuilder MapKybEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/kyb")
            .WithTags("KYB")

            // Every route here reads or writes the caller's own agency. Without authentication
            // there is no tenant, and the query filters would return nothing — a confusing empty
            // screen rather than an honest 401.
            .RequireAuthorization();

        group.MapGet("/status", async (GetKybStatusHandler handler, CancellationToken cancellationToken) =>
                Results.Ok(await handler.HandleAsync(cancellationToken)))
            .WithName("GetKybStatus")
            .Produces<KybStatusResponse>();

        // Served so the browser can reject a bad file immediately instead of after a slow upload.
        // The server enforces the same limits regardless.
        group.MapGet("/limits", () => Results.Ok(new KybUploadLimitsResponse(
                KybDocumentRules.MaxSizeBytes,
                KybDocumentRules.AllowedContentTypes,
                KybDocumentRules.AllowedExtensions,
                KybSubmission.RequiredDocuments.Select(d => d.ToString()).ToList())))
            .WithName("GetKybUploadLimits")
            .Produces<KybUploadLimitsResponse>();

        group.MapPost("/documents", async (
                HttpRequest request,
                UploadKybDocumentHandler handler,
                CancellationToken cancellationToken) =>
            {
                if (!request.HasFormContentType)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Send the document as multipart/form-data.");
                }

                var form = await request.ReadFormAsync(cancellationToken);
                var file = form.Files.GetFile("file");

                if (file is null || file.Length == 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "No file was attached.");
                }

                if (!Enum.TryParse<KybDocumentType>(form["documentType"], ignoreCase: true, out var documentType))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Say which document this is.",
                        detail: $"documentType must be one of: {string.Join(", ", Enum.GetNames<KybDocumentType>())}.");
                }

                await using var content = file.OpenReadStream();

                var outcome = await handler.HandleAsync(
                    documentType, file.FileName, file.Length, content, cancellationToken);

                return outcome switch
                {
                    UploadKybDocumentOutcome.Uploaded uploaded => Results.Ok(new KybDocumentResponse(
                        uploaded.Document.Id,
                        uploaded.Document.DocumentType.ToString(),
                        uploaded.Document.FileName,
                        uploaded.Document.ContentType,
                        uploaded.Document.SizeBytes,
                        uploaded.Document.CreatedAt)),

                    // 409, not 400: the request was fine, the submission's state was not.
                    UploadKybDocumentOutcome.SubmissionLocked => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Your documents are with Trips for review.",
                        detail: "You can upload again if the submission is returned to you."),

                    UploadKybDocumentOutcome.Rejected rejected => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: rejected.Reason,
                        detail: $"Accepted: {string.Join(", ", KybDocumentRules.AllowedExtensions)}, "
                                + $"up to {KybDocumentRules.MaxSizeDescription}."),

                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .WithName("UploadKybDocument")
            .Produces<KybDocumentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery()

            // Refused at the edge rather than buffered and then rejected. The handler checks the
            // written length again, because a client can under-report Content-Length.
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(KybDocumentRules.MaxSizeBytes));

        group.MapPost("/submit", async (SubmitKybHandler handler, CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(cancellationToken);

                return outcome switch
                {
                    SubmitKybOutcome.Submitted => Results.Accepted(value: new
                    {
                        message = "Your documents are with Trips. Most reviews are finished within "
                                  + "one working day, and we'll email you as soon as there's a decision.",
                    }),

                    SubmitKybOutcome.Incomplete incomplete => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Some required documents are missing.",
                        detail: $"Still needed: {string.Join(", ", incomplete.MissingDocumentTypes)}."),

                    _ => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "There is nothing to submit."),
                };
            })
            .WithName("SubmitKyb")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
