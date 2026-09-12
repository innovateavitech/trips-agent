using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Api.Storefront;

/// <summary>
/// The anonymous API the traveller-facing storefront reads (issue 60).
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous, and host-resolved.</b> There is no token here — travellers do not have accounts
/// (MVP decision 21). Which agency is served is decided by the request's <c>Host</c> and by nothing
/// else: no query parameter, header or path segment can choose it. Behind the ingress the traveller's
/// host arrives as the forwarded host, which is believed only from a proxy we run
/// (<c>ForwardedHeadersSetup</c>).
/// </para>
/// <para>
/// <b>Cached hard.</b> Every response here is the same for every traveller on that hostname, and the
/// storefront regenerates its pages when a site is published rather than waiting out a TTL, so the
/// cache headers are generous and <c>stale-while-revalidate</c> keeps a page serving while the next
/// one is fetched.
/// </para>
/// <para>
/// <b>Nothing here mentions the platform</b> (CLAUDE.md rule 4), and nothing carries a net rate, a
/// markup or an internal id.
/// </para>
/// </remarks>
public static class PublicStorefrontEndpoints
{
    /// <summary>The header a preview link's token arrives in, set by the storefront, never by a traveller.</summary>
    public const string PreviewTokenHeader = "X-Storefront-Preview";

    /// <summary>How long a traveller-facing response may be reused, in seconds.</summary>
    private const int CacheSeconds = 60;

    /// <summary>How long a stale response may still be served while a fresh one is fetched.</summary>
    private const int StaleSeconds = 600;

    public static IEndpointRouteBuilder MapPublicStorefrontEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/public/storefront")
            .WithTags("Public storefront")
            .AllowAnonymous();

        group.MapGet("/site", async (
                HttpContext http,
                PublicSiteResolver resolver,
                PublicSiteService sites,
                CancellationToken cancellationToken) =>
            {
                var route = await resolver.ResolveAsync(http.Request.Host.Value, cancellationToken);

                if (route is null)
                {
                    return UnknownHost();
                }

                var site = await sites.GetAsync(route, PreviewTokenOf(http), cancellationToken);

                if (site is null)
                {
                    return UnknownHost();
                }

                Cache(http);

                return Results.Ok(site);
            })
            .WithName("GetPublicSite")
            .Produces<PublicSiteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/catalog", async (
                HttpContext http,
                PublicSiteResolver resolver,
                PublicCatalogService catalog,
                CancellationToken cancellationToken,
                [FromQuery] string? type = null,
                [FromQuery] string? destination = null,
                [FromQuery] string? category = null,
                [FromQuery] long? minPrice = null,
                [FromQuery] long? maxPrice = null,
                [FromQuery] string? q = null,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = PublicCatalogService.DefaultPageSize) =>
            {
                var route = await resolver.ResolveAsync(http.Request.Host.Value, cancellationToken);

                if (route is null || !route.Serveable)
                {
                    return UnknownHost();
                }

                var results = await catalog.BrowseAsync(
                    new PublicCatalogQuery(type, destination, category, minPrice, maxPrice, q, page, pageSize),
                    cancellationToken);

                Cache(http);

                return Results.Ok(results);
            })
            .WithName("BrowsePublicCatalog")
            .Produces<PublicCatalogResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/catalog/{slug}", async (
                string slug,
                HttpContext http,
                PublicSiteResolver resolver,
                PublicCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var route = await resolver.ResolveAsync(http.Request.Host.Value, cancellationToken);

                if (route is null || !route.Serveable)
                {
                    return UnknownHost();
                }

                var product = await catalog.GetAsync(slug, cancellationToken);

                if (product is null)
                {
                    return Results.Problem(
                        title: "No such page",
                        detail: "This product is not on sale.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                Cache(http);

                return Results.Ok(product);
            })
            .WithName("GetPublicProduct")
            .Produces<PublicProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/sitemap", async (
                HttpContext http,
                PublicSiteResolver resolver,
                PublicSiteService sites,
                PublicCatalogService catalog,
                CancellationToken cancellationToken) =>
            {
                var route = await resolver.ResolveAsync(http.Request.Host.Value, cancellationToken);

                if (route is null || !route.Serveable)
                {
                    return UnknownHost();
                }

                var site = await sites.GetAsync(route, previewToken: null, cancellationToken);

                // Only a live site has anything worth listing. Anything else gets an empty sitemap
                // rather than a 404, so the storefront can always serve the file.
                if (site is null || site.Status != PublicSiteStatuses.Live)
                {
                    return Results.Ok(new PublicSitemapResponse(sites.BaseUrlFor(route), []));
                }

                var entries = new List<PublicSitemapEntry>();
                var changedAt = site.PublishedAt ?? DateTimeOffset.UnixEpoch;

                foreach (var pageEntry in site.Content.Pages)
                {
                    entries.Add(new PublicSitemapEntry(
                        pageEntry.Slug == SitePageRules.HomeSlug ? "/" : $"/{pageEntry.Slug}",
                        changedAt,
                        "weekly"));
                }

                foreach (var (slug, updatedAt) in await catalog.SitemapEntriesAsync(cancellationToken))
                {
                    entries.Add(new PublicSitemapEntry($"/{SiteTemplateCatalog.CatalogSlug}/{slug}", updatedAt, "weekly"));
                }

                Cache(http);

                return Results.Ok(new PublicSitemapResponse(sites.BaseUrlFor(route), entries));
            })
            .WithName("GetPublicSitemap")
            .Produces<PublicSitemapResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// No site answers on this hostname. Deliberately the same answer for a name nobody has claimed
    /// and one claimed but not yet proved: neither tells an outsider anything about the other.
    /// </summary>
    private static IResult UnknownHost() =>
        Results.Problem(
            title: "No website here",
            detail: "No website is set up on this address.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The preview token, from a header the storefront sets after reading it from the link. It can
    /// only ever widen what is shown of the site it was issued for — never which agency is served.
    /// </summary>
    private static string? PreviewTokenOf(HttpContext http) =>
        http.Request.Headers.TryGetValue(PreviewTokenHeader, out var values) ? values.ToString() : null;

    /// <summary>
    /// Lets the storefront and anything in front of it reuse this response, and keep serving the old
    /// one while it fetches a new one. Varies on Host, because the whole answer depends on it.
    /// </summary>
    private static void Cache(HttpContext http)
    {
        http.Response.Headers.CacheControl =
            $"public, max-age={CacheSeconds}, stale-while-revalidate={StaleSeconds}";

        http.Response.Headers[HeaderNames.Vary] = HeaderNames.Host;
    }
}
