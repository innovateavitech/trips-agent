using TripsAgent.Api.Authorization;
using TripsAgent.Application.Billing;
using TripsAgent.Contracts.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Billing;

/// <summary>
/// The back office's subscription tiers (issue 64).
/// </summary>
/// <remarks>
/// <para>
/// Every route needs <c>subscription.manage</c>, which <c>PermissionCodes.PlatformOnly</c> forbids
/// any agency role from holding. Tiers are the platform's, and there is no agency-facing route into
/// this file at all — the plan picker reads a projection through <see cref="SubscriptionEndpoints"/>
/// that carries no internal fields.
/// </para>
/// <para>
/// Every write demands a reason of at least ten characters. The audit trail is written by the save
/// interceptor rather than by these routes, so it cannot be forgotten.
/// </para>
/// </remarks>
public static class TierAdminEndpoints
{
    public static IEndpointRouteBuilder MapTierAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/billing")
            .WithTags("Subscription administration")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.SubscriptionManage));

        group.MapGet("/entitlements", async (TierAdminService tiers, CancellationToken cancellationToken) =>
            {
                var catalogue = await tiers.CatalogueListAsync(cancellationToken);

                return Results.Ok(catalogue.Select(entitlement => new EntitlementCatalogueItem(
                    entitlement.Code,
                    entitlement.Name,
                    entitlement.Description,
                    entitlement.ValueType.ToString(),
                    EntitlementCatalog.Fallback(entitlement.Code).ToJson())).ToList());
            })
            .WithName("EntitlementCatalogue")
            .Produces<List<EntitlementCatalogueItem>>();

        group.MapGet("/tiers", async (
                bool? includeArchived,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                var all = await tiers.ListAsync(includeArchived ?? true, cancellationToken);

                return Results.Ok(all.Select(ToResponse).ToList());
            })
            .WithName("ListSubscriptionTiers")
            .Produces<List<TierResponse>>();

        group.MapGet("/tiers/{tierId:guid}", async (
                Guid tierId,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                var tier = await tiers.GetAsync(tierId, cancellationToken);

                return tier is null ? Results.NotFound() : Results.Ok(ToResponse(tier));
            })
            .WithName("GetSubscriptionTier")
            .Produces<TierResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tiers", async (
                SaveTierRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var draft = new TierDraft(
                    request.Code, request.Name, request.CustomerDescription,
                    request.TrialDays, request.SortOrder, request.IsFallback);

                return ToResult(await tiers.CreateAsync(draft, request.Reason, cancellationToken), created: true);
            })
            .WithName("CreateSubscriptionTier")
            .Produces<TierChangeResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/tiers/{tierId:guid}", async (
                Guid tierId,
                SaveTierRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var draft = new TierDraft(
                    request.Code, request.Name, request.CustomerDescription,
                    request.TrialDays, request.SortOrder, request.IsFallback);

                return ToResult(await tiers.UpdateAsync(tierId, draft, request.Reason, cancellationToken));
            })
            .WithName("UpdateSubscriptionTier")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/tiers/{tierId:guid}/price", async (
                Guid tierId,
                SetTierPriceRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                if (!Enum.TryParse<BillingInterval>(request.Interval, ignoreCase: false, out var interval))
                {
                    return Invalid("interval", request.Interval, Enum.GetNames<BillingInterval>());
                }

                return ToResult(await tiers.SetPriceAsync(
                    tierId, request.Currency, interval, request.AmountMinor, request.Reason, cancellationToken));
            })
            .WithName("SetSubscriptionTierPrice")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/tiers/{tierId:guid}/entitlements", async (
                Guid tierId,
                SetTierEntitlementsRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var grants = request.Entitlements
                    .Select(entitlement => new EntitlementGrant(entitlement.Code, entitlement.Value))
                    .ToList();

                return ToResult(await tiers.SetEntitlementsAsync(tierId, grants, request.Reason, cancellationToken));
            })
            .WithName("SetSubscriptionTierEntitlements")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tiers/{tierId:guid}/publish", async (
                Guid tierId,
                TierReasonRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await tiers.PublishAsync(tierId, request.Reason, cancellationToken)))
            .WithName("PublishSubscriptionTier")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/tiers/{tierId:guid}/archive", async (
                Guid tierId,
                TierReasonRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await tiers.ArchiveAsync(tierId, request.Reason, cancellationToken)))
            .WithName("ArchiveSubscriptionTier")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/tiers/{tierId:guid}/restore", async (
                Guid tierId,
                TierReasonRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await tiers.RestoreAsync(tierId, request.Reason, cancellationToken)))
            .WithName("RestoreSubscriptionTier")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // DELETE, and almost every call to it is refused. A tier with subscribers, or one that has
        // ever been published, can only be archived — FRD RS-6. The route exists so that throwing
        // away a draft created by mistake does not need a database console.
        group.MapDelete("/tiers/{tierId:guid}", async (
                Guid tierId,
                string? reason,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
                ToResult(await tiers.DeleteAsync(tierId, reason ?? string.Empty, cancellationToken)))
            .WithName("DeleteSubscriptionTier")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/tiers/{tierId:guid}/migrate-subscribers", async (
                Guid tierId,
                MigrateSubscribersRequest request,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await tiers.MigrateSubscribersAsync(
                        tierId, request.ToTierId, request.Reason, cancellationToken)))
            .WithName("MigrateSubscriptionTierSubscribers")
            .Produces<TierChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/subscribers", async (
                Guid? tierId,
                TierAdminService tiers,
                CancellationToken cancellationToken) =>
            {
                var subscribers = await tiers.SubscribersAsync(tierId, cancellationToken);

                return Results.Ok(subscribers.Select(subscriber => new SubscriberResponse(
                    subscriber.AgencyId,
                    subscriber.AgencyName,
                    subscriber.AgencyStatus,
                    subscriber.SubscriptionId,
                    subscriber.TierName,
                    subscriber.Status.ToString(),
                    subscriber.Currency,
                    subscriber.AmountMinor,
                    subscriber.CurrentPeriodStart,
                    subscriber.CurrentPeriodEnd,
                    subscriber.TrialEndsAt,
                    subscriber.DunningRetries,
                    subscriber.NextDunningAttemptAt,
                    subscriber.OutstandingMinor)).ToList());
            })
            .WithName("ListSubscribers")
            .Produces<List<SubscriberResponse>>();

        return app;
    }

    private static IResult ToResult(TierChangeOutcome outcome, bool created = false) => outcome switch
    {
        TierChangeOutcome.Saved saved when created => Results.Created(
            $"/api/v1/admin/billing/tiers/{saved.Tier.Id}",
            new TierChangeResponse(ToResponse(saved.Tier), saved.SubscribersScheduled)),

        TierChangeOutcome.Saved saved => Results.Ok(
            new TierChangeResponse(ToResponse(saved.Tier), saved.SubscribersScheduled)),

        TierChangeOutcome.Invalid invalid => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "That tier change does not add up.",
            detail: invalid.Reason),

        TierChangeOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No subscription tier with that id."),

        TierChangeOutcome.Refused refused => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That tier cannot be changed this way.",
            detail: refused.Reason),

        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
    };

    private static TierResponse ToResponse(TierView tier) =>
        new(
            tier.Id,
            tier.Code,
            tier.Name,
            tier.CustomerDescription,
            tier.Status.ToString(),
            tier.TrialDays,
            tier.SortOrder,
            tier.IsFallback,
            tier.Subscribers,
            tier.PublishedAt,
            tier.ArchivedAt,
            [
                .. tier.Prices.Select(price => new TierPriceResponse(
                    price.Id,
                    price.Currency,
                    price.Interval.ToString(),
                    price.AmountMinor,
                    price.IsPromotional,
                    price.EffectiveFrom,
                    price.EffectiveTo)),
            ],
            [
                .. tier.Entitlements.Select(entitlement => new TierEntitlementResponse(
                    entitlement.Code,
                    entitlement.Name,
                    entitlement.ValueType.ToString(),
                    entitlement.Value,
                    entitlement.Display)),
            ]);

    /// <summary>
    /// A value nobody recognises is a 400, never a silent default.
    /// </summary>
    private static IResult Invalid(string field, string? value, string[] allowed) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = [$"'{value}' is not one of: {string.Join(", ", allowed)}."],
        });
}
