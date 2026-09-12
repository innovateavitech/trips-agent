using TripsAgent.Api.Authorization;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Storefront;

/// <summary>
/// A website's addresses, for the agent console (issue 59), and the platform's queue of addresses set aside
/// for review (open question 20).
/// </summary>
/// <remarks>
/// <c>storefront.edit</c> to see the addresses and ask for a check; <c>storefront.publish</c> to connect, remove
/// or promote one, because those change where travellers find the site. The review queue reads every agency's
/// addresses, so it needs the platform's <c>agency.manage</c>.
/// </remarks>
public static class SiteDomainEndpoints
{
    public static IEndpointRouteBuilder MapSiteDomainEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/storefront/domains")
            .WithTags("Storefront")
            .RequireAuthorization();

        var edit = PermissionPolicies.For(PermissionCodes.StorefrontEdit);
        var publish = PermissionPolicies.For(PermissionCodes.StorefrontPublish);

        group.MapGet(string.Empty, async (SiteDomainService domains, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(await domains.ListAsync(cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("ListSiteDomains")
            .Produces<List<SiteDomainResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(string.Empty, async (AddSiteDomainRequest request, SiteDomainService domains, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(
                    await domains.AddAsync(request, cancellationToken),
                    domain => Results.Created($"/api/v1/storefront/domains/{domain.Id}", domain)))
            .RequireAuthorization(publish)
            .WithName("AddSiteDomain")
            .Produces<SiteDomainResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{domainId:guid}", async (Guid domainId, SiteDomainService domains, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(await domains.RemoveAsync(domainId, cancellationToken), _ => Results.NoContent()))
            .RequireAuthorization(publish)
            .WithName("RemoveSiteDomain")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{domainId:guid}/primary", async (Guid domainId, SiteDomainService domains, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(await domains.MakePrimaryAsync(domainId, cancellationToken)))
            .RequireAuthorization(publish)
            .WithName("MakeSiteDomainPrimary")
            .Produces<SiteDomainResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // For an agent who has just created the records: look now rather than at the next scheduled check.
        group.MapPost("/{domainId:guid}/check", async (Guid domainId, SiteDomainService domains, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(await domains.CheckNowAsync(domainId, cancellationToken)))
            .RequireAuthorization(edit)
            .WithName("CheckSiteDomain")
            .Produces<SiteDomainResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var review = app.MapGroup("/api/v1/admin/hostname-reviews")
            .WithTags("Hostname review")

            // Every route below reads or clears another agency's address, so the platform permission is
            // required rather than merely authentication.
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.AgencyManage));

        review.MapGet(string.Empty, async (HostnameReviewService reviews, CancellationToken cancellationToken) =>
                Results.Ok(await reviews.ListAsync(cancellationToken)))
            .WithName("ListHostnameReviews")
            .Produces<List<HostnameReviewResponse>>();

        review.MapPost("/{domainId:guid}/approve", async (Guid domainId, HostnameReviewService reviews, CancellationToken cancellationToken) =>
                StorefrontEndpoints.ToResult(await reviews.ApproveAsync(domainId, cancellationToken)))
            .WithName("ApproveHostname")
            .Produces<HostnameReviewResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
