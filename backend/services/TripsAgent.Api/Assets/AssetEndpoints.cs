using TripsAgent.Application.Assets;
using TripsAgent.Contracts.Assets;
using TripsAgent.Domain.Assets;

namespace TripsAgent.Api.Assets;

/// <summary>
/// Uploading files: ask for a signed URL, send the file straight to storage, say it is done, read
/// it back once it is scanned. Issue #18.
/// </summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/assets")
            .WithTags("Assets")

            // Every route reads or writes the caller's own agency's files. Without authentication
            // there is no tenant, and the filters would return nothing — a confusing 404 rather
            // than an honest 401.
            .RequireAuthorization();

        group.MapGet("/limits", () => Results.Ok(new AssetUploadLimitsResponse(
                Enum.GetValues<AssetPurpose>()

                    // Generated documents are rendered by the platform, so they have no upload limits to show.
                    .Where(AssetRules.IsUploadable)
                    .Select(purpose => new AssetPurposeLimitsResponse(
                        purpose.ToString(),
                        AssetRules.MaxSizeBytes(purpose),
                        AssetRules.AllowedContentTypes(purpose),
                        AssetRules.AllowedExtensions(purpose)))
                    .ToList())))
            .WithName("GetAssetUploadLimits")
            .Produces<AssetUploadLimitsResponse>();

        group.MapPost("/uploads", async (
                RequestAssetUploadRequest request,
                RequestAssetUploadHandler handler,
                CancellationToken cancellationToken) =>
            {
                // IsDefined as well as TryParse: TryParse accepts any number, so "7" would parse
                // into a purpose that does not exist.
                if (!Enum.TryParse<AssetPurpose>(request.Purpose, ignoreCase: true, out var purpose)
                    || !AssetRules.IsUploadable(purpose))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Say what the file is for.",
                        detail: "purpose must be one of: "
                                + $"{string.Join(", ", Enum.GetValues<AssetPurpose>().Where(AssetRules.IsUploadable))}.");
                }

                var outcome = await handler.HandleAsync(
                    purpose, request.FileName, request.SizeBytes, request.ContentType, cancellationToken);

                return outcome switch
                {
                    RequestAssetUploadOutcome.Reserved reserved => Results.Ok(new AssetUploadResponse(
                        reserved.Asset.Id,
                        reserved.Upload.Url,
                        reserved.Upload.Method,
                        reserved.Upload.Headers,
                        reserved.Upload.MaxSizeBytes,
                        reserved.Upload.ExpiresAt)),

                    RequestAssetUploadOutcome.Rejected rejected => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: rejected.Reason,
                        detail: $"Accepted: {string.Join(", ", AssetRules.AllowedExtensions(purpose))}, "
                                + $"up to {AssetRules.SizeDescription(purpose)}."),

                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .WithName("RequestAssetUpload")
            .Produces<AssetUploadResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/{assetId:guid}/complete", async (
                Guid assetId,
                CompleteAssetUploadHandler handler,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(assetId, cancellationToken);

                return outcome switch
                {
                    // 202: accepted for scanning, not yet usable. The links arrive once it is Ready.
                    CompleteAssetUploadOutcome.Accepted accepted => Results.Accepted(
                        $"/api/v1/assets/{accepted.Asset.Id}",
                        ToResponse(accepted.Asset, [])),

                    CompleteAssetUploadOutcome.NotFound => Results.NotFound(),

                    // 409, not 400: the request was fine, the upload simply has not landed yet.
                    CompleteAssetUploadOutcome.NotArrived => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "The file has not arrived yet.",
                        detail: "Finish sending it to the upload URL, then try again."),

                    CompleteAssetUploadOutcome.Rejected rejected => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: rejected.Reason,
                        detail: "Start a new upload with a different file."),

                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .WithName("CompleteAssetUpload")
            .Produces<AssetResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{assetId:guid}", async (
                Guid assetId,
                GetAssetHandler handler,
                CancellationToken cancellationToken) =>
            {
                var view = await handler.HandleAsync(assetId, cancellationToken);

                return view is null
                    ? Results.NotFound()
                    : Results.Ok(ToResponse(view.Asset, view.Links));
            })
            .WithName("GetAsset")
            .Produces<AssetResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static AssetResponse ToResponse(Asset asset, IReadOnlyList<AssetLink> links) => new(
        asset.Id,
        asset.Purpose.ToString(),
        asset.FileName,
        asset.Status.ToString(),
        asset.ScanStatus.ToString(),
        asset.ContentType,
        asset.SizeBytes,
        asset.Width,
        asset.Height,
        asset.FailureReason,
        asset.CreatedAt,
        links.Select(link => new AssetLinkResponse(
                link.Kind.ToString(),
                link.Url,
                link.ContentType,
                link.Width,
                link.Height,
                link.ExpiresAt))
            .ToList());
}
