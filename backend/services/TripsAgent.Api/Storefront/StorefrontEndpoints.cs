using TripsAgent.Api.Authorization;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Storefront;

/// <summary>
/// The website builder, for the agent console: templates, the site and its settings, its look, its pages,
/// and staging, publishing and rolling back its versions (issue 58).
/// </summary>
/// <remarks>
/// <c>storefront.edit</c> to change the draft; <c>storefront.publish</c> to change what travellers see.
/// Every read and write is tenant-filtered — an agency can only ever reach its own site.
/// </remarks>
public static class StorefrontEndpoints
{
    public static IEndpointRouteBuilder MapStorefrontEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/storefront")
            .WithTags("Storefront")
            .RequireAuthorization();

        var edit = PermissionPolicies.For(PermissionCodes.StorefrontEdit);
        var publish = PermissionPolicies.For(PermissionCodes.StorefrontPublish);

        group.MapGet("/templates", async (SiteBuilderService builder, CancellationToken cancellationToken) =>
                Results.Ok(await builder.ListTemplatesAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("ListSiteTemplates")
            .Produces<List<SiteTemplateResponse>>();

        group.MapGet("/site", async (SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.GetSiteAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("GetSite")
            .Produces<SiteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/site", async (CreateSiteRequest request, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(
                    await builder.CreateSiteAsync(request, cancellationToken),
                    site => Results.Created("/api/v1/storefront/site", site)))
            .RequireAuthorization(edit)
            .WithName("CreateSite")
            .Produces<SiteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/site/settings", async (SiteSettingsRequest request, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.UpdateSettingsAsync(request, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("UpdateSiteSettings")
            .Produces<SiteResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/theme", async (SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.GetThemeAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("GetSiteTheme")
            .Produces<SiteThemeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/theme", async (SiteThemeRequest request, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.SaveThemeAsync(request, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("SaveSiteTheme")
            .Produces<SiteThemeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/pages/{pageId:guid}", async (Guid pageId, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.GetPageAsync(pageId, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("GetSitePage")
            .Produces<SitePageResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/pages", async (CreateSitePageRequest request, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(
                    await builder.CreatePageAsync(request, cancellationToken),
                    page => Results.Created($"/api/v1/storefront/pages/{page.Id}", page)))
            .RequireAuthorization(edit)
            .WithName("CreateSitePage")
            .Produces<SitePageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The whole page in one request: details and every block, in order. A stale revision is a 409.
        group.MapPut("/pages/{pageId:guid}", async (
                Guid pageId,
                SaveSitePageRequest request,
                SiteBuilderService builder,
                CancellationToken cancellationToken) =>
                ToResult(await builder.SavePageAsync(pageId, request, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("SaveSitePage")
            .Produces<SitePageResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/pages/{pageId:guid}", async (Guid pageId, SiteBuilderService builder, CancellationToken cancellationToken) =>
                ToResult(await builder.DeletePageAsync(pageId, cancellationToken), _ => Results.NoContent()))
            .RequireAuthorization(edit)
            .WithName("DeleteSitePage")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/versions", async (SiteVersionService versions, CancellationToken cancellationToken) =>
                ToResult(await versions.ListVersionsAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("ListSiteVersions")
            .Produces<List<SiteVersionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/versions/stage", async (SiteVersionService versions, CancellationToken cancellationToken) =>
                ToResult(await versions.StageAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("StageSite")
            .Produces<SiteVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // 422 carries a "problems" list — every reason the publish gate refused, for the console to show.
        group.MapPost("/versions/{versionId:guid}/publish", async (
                Guid versionId,
                SiteVersionService versions,
                CancellationToken cancellationToken) =>
                ToResult(await versions.PublishAsync(versionId, cancellationToken)))
            .RequireAuthorization(publish)
            .WithName("PublishSiteVersion")
            .Produces<SiteVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/versions/{versionId:guid}/rollback", async (
                Guid versionId,
                SiteVersionService versions,
                CancellationToken cancellationToken) =>
                ToResult(await versions.RollBackAsync(versionId, cancellationToken)))
            .RequireAuthorization(publish)
            .WithName("RollBackSiteVersion")
            .Produces<SiteVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/preview-links", async (
                SitePreviewLinkRequest request,
                SiteVersionService versions,
                CancellationToken cancellationToken) =>
                ToResult(await versions.PreviewLinkAsync(request.VersionId, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("CreateSitePreviewLink")
            .Produces<SitePreviewLinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The site's addresses and the platform's review queue for them live next door.
        app.MapSiteDomainEndpoints();

        return app;
    }

    /// <summary>A builder outcome as an HTTP response: problem details, in the API's own plain language.</summary>
    internal static IResult ToResult<T>(StorefrontResult<T> result, Func<T, IResult>? onOk = null) => result switch
    {
        StorefrontResult<T>.Ok ok => onOk is null ? Results.Ok(ok.Value) : onOk(ok.Value),

        StorefrontResult<T>.NotFound notFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: notFound.Title),

        StorefrontResult<T>.Invalid invalid => Results.ValidationProblem(
            invalid.Errors.ToDictionary(pair => pair.Key, pair => pair.Value),
            title: "Some details need fixing."),

        StorefrontResult<T>.Conflict conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: conflict.Title,
            detail: conflict.Detail),

        StorefrontResult<T>.Refused refused => Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: refused.Title,
            detail: refused.Detail,
            extensions: new Dictionary<string, object?>
            {
                ["problems"] = refused.Problems.Select(problem => new SitePublishProblemResponse(problem.Code, problem.Message)).ToList(),
            }),

        _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
    };
}
