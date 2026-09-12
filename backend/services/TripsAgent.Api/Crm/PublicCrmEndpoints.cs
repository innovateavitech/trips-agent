using TripsAgent.Api.RateLimiting;
using TripsAgent.Application.Crm;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Contracts.Crm;

namespace TripsAgent.Api.Crm;

/// <summary>
/// The CRM's anonymous, traveller-facing routes (#62): the storefront's trip-request widget, and the
/// quote page a customer opens from the link in their email.
/// </summary>
/// <remarks>
/// <para>
/// <b>No token, and no tenant.</b> A traveller on an agency's own site is signed in to nothing, so
/// the agency is worked out from the host name their browser used — <c>X-Storefront-Host</c> where the
/// storefront sets it, and otherwise the request's own <c>Host</c> — and every read and write after
/// that happens inside that agency's tenant filter. A host nobody's storefront answers on gets the
/// same 404 as a quote that does not exist, so nothing here tells an anonymous caller which agencies
/// are on the platform.
/// </para>
/// <para>
/// <b>Nothing here mentions Trips</b> (CLAUDE.md rule 4): the quote page carries the agency's own
/// quote, for the agency's own customer, reached at the agency's own domain.
/// </para>
/// <para>
/// Rate-limited per calling address under the <c>Storefront</c> policy: these are the only CRM routes
/// anyone on the internet can reach, and each one writes.
/// </para>
/// </remarks>
public static class PublicCrmEndpoints
{
    /// <summary>The header the storefront sends the traveller's host name in, ahead of its own proxy hops.</summary>
    public const string StorefrontHostHeader = "X-Storefront-Host";

    public static IEndpointRouteBuilder MapPublicCrmEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/public/crm")
            .WithTags("Storefront CRM")
            .AllowAnonymous()
            .RequireRateLimitPolicy(RateLimitPolicyNames.Storefront);

        group.MapPost("/trip-requests", async (
                HttpContext http,
                TripRequestSubmission submission,
                StorefrontCrmService storefront,
                CancellationToken cancellationToken) =>
            {
                var outcome = await storefront.SubmitTripRequestAsync(HostOf(http), submission, cancellationToken);

                // Nothing comes back but "thank you": the traveller has no business seeing the lead
                // they just made, and the agent picks it up in their inbox.
                return outcome is CrmResult<Guid>.Done ? Results.Accepted() : CrmEndpoints.ToResult(outcome);
            })
            .WithName("SubmitTripRequest")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Reading the quote is what records that the customer saw it — the first time only.
        group.MapGet("/quotes/{token}", async (
                string token,
                HttpContext http,
                StorefrontCrmService storefront,
                CancellationToken cancellationToken) =>
                CrmEndpoints.ToResult(await storefront.ViewQuoteAsync(HostOf(http), token, cancellationToken)))
            .WithName("ViewPublicQuote")
            .Produces<PublicQuoteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/quotes/{token}/accept", async (
                string token,
                HttpContext http,
                StorefrontCrmService storefront,
                CancellationToken cancellationToken) =>
                CrmEndpoints.ToResult(await storefront.AcceptQuoteAsync(HostOf(http), token, cancellationToken)))
            .WithName("AcceptPublicQuote")
            .Produces<PublicQuoteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/quotes/{token}/decline", async (
                string token,
                HttpContext http,
                DeclineQuoteRequest? request,
                StorefrontCrmService storefront,
                CancellationToken cancellationToken) =>
                CrmEndpoints.ToResult(
                    await storefront.DeclineQuoteAsync(HostOf(http), token, request?.Reason, cancellationToken)))
            .WithName("DeclinePublicQuote")
            .Produces<PublicQuoteResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// The host name the traveller's browser used: the storefront's header where it set one, and
    /// otherwise this request's own <c>Host</c>.
    /// </summary>
    /// <remarks>
    /// The storefront renders the page and calls this API server-side, so its own <c>Host</c> is the
    /// API's, not the traveller's. The header is how it passes the one that matters. It decides only
    /// <em>which agency's</em> data is read — never whether the caller may read it — so a forged
    /// header buys nothing that asking that agency's own domain would not.
    /// </remarks>
    private static string? HostOf(HttpContext http)
    {
        var header = http.Request.Headers[StorefrontHostHeader].ToString();

        return StorefrontCrmService.NormaliseHost(
            string.IsNullOrWhiteSpace(header) ? http.Request.Host.Value : header);
    }
}
