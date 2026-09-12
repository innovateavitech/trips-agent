using TripsAgent.Api.Authorization;
using TripsAgent.Application.Billing;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Billing;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Billing;

/// <summary>
/// An agency's own plan, its invoices and its receipts (issues 64 and 65).
/// </summary>
/// <remarks>
/// <para>
/// Reading is <c>billing.view</c>, which a Manager holds; changing the plan is
/// <c>billing.manage</c>, which only the Owner does. The split is deliberate: knowing what the
/// agency pays is part of running it, and committing it to a different monthly bill is not.
/// </para>
/// <para>
/// Nothing here accepts a card number and nothing it calls can. A first payment goes to Paystack's
/// hosted page and comes back as a reusable authorisation; renewals charge that. Build-plan
/// decision 18.
/// </para>
/// </remarks>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/billing")
            .WithTags("Billing")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BillingView))
            .AddEndpointFilter(RequireAgencyAsync);

        group.MapGet("/plans", async (SubscriptionService subscriptions, CancellationToken cancellationToken) =>
            {
                var plans = await subscriptions.PlansAsync(cancellationToken);

                return Results.Ok(plans.Select(plan => new PlanResponse(
                    plan.TierId,
                    plan.Code,
                    plan.Name,
                    plan.Description,
                    plan.Currency,
                    plan.AmountMinor,
                    plan.Interval.ToString(),
                    plan.TrialDays,
                    plan.IsCurrent,
                    plan.IsFallback,
                    ToFeatures(plan.Features))).ToList());
            })
            .WithName("ListPlans")
            .Produces<List<PlanResponse>>();

        group.MapGet("/subscription", async (SubscriptionService subscriptions, CancellationToken cancellationToken) =>
                Results.Ok(ToResponse(await subscriptions.MineAsync(cancellationToken))))
            .WithName("GetMyPlan")
            .Produces<MyPlanResponse>();

        group.MapGet("/entitlements", async (
                IEntitlements entitlements,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                var resolved = await entitlements.ForAsync(tenant.AgencyId!.Value, cancellationToken);

                return Results.Ok(new MyEntitlementsResponse(
                    resolved.TierId,
                    resolved.TierName ?? "No plan",
                    [
                        .. resolved.All
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => new PlanFeatureResponse(
                                pair.Key,
                                Domain.Billing.EntitlementCatalog.Definition(pair.Key).Name,
                                pair.Value.ToString())),
                    ]));
            })
            .WithName("GetMyEntitlements")
            .Produces<MyEntitlementsResponse>();

        group.MapGet("/invoices", async (
                int? take,
                SubscriptionService subscriptions,
                CancellationToken cancellationToken) =>
            {
                var invoices = await subscriptions.InvoicesAsync(take ?? 50, cancellationToken);

                return Results.Ok(invoices.Select(ToResponse).ToList());
            })
            .WithName("ListSubscriptionInvoices")
            .Produces<List<SubscriptionInvoiceResponse>>();

        group.MapGet("/invoices/{invoiceId:guid}", async (
                Guid invoiceId,
                SubscriptionService subscriptions,
                CancellationToken cancellationToken) =>
            {
                var invoice = await subscriptions.InvoiceAsync(invoiceId, cancellationToken);

                return invoice is null ? Results.NotFound() : Results.Ok(ToResponse(invoice));
            })
            .WithName("GetSubscriptionInvoice")
            .Produces<SubscriptionInvoiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/subscription", async (
                ChoosePlanRequest request,
                SubscriptionService subscriptions,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await subscriptions.ChoosePlanAsync(
                        request.TierId, request.CallbackUrl, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BillingManage))
            .WithName("ChoosePlan")
            .Produces<PlanChangeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/subscription/complete", async (
                CompleteCheckoutRequest request,
                SubscriptionService subscriptions,
                CancellationToken cancellationToken) =>
                request is null
                    ? Results.BadRequest()
                    : ToResult(await subscriptions.CompleteCheckoutAsync(request.Reference, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BillingManage))
            .WithName("CompletePlanCheckout")
            .Produces<PlanChangeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/subscription/scheduled-change/{migrationId:guid}", async (
                Guid migrationId,
                SubscriptionService subscriptions,
                CancellationToken cancellationToken) =>
                ToResult(await subscriptions.CancelScheduledChangeAsync(migrationId, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BillingManage))
            .WithName("CancelScheduledPlanChange")
            .Produces<PlanChangeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static IResult ToResult(PlanChangeOutcome outcome) => outcome switch
    {
        PlanChangeOutcome.Applied applied => Results.Ok(
            new PlanChangeResponse("Applied", ToResponse(applied.Subscription), null, null, null, null)),

        PlanChangeOutcome.PaymentRequired payment => Results.Ok(
            new PlanChangeResponse(
                "PaymentRequired", null, payment.AuthorizationUrl, payment.Reference, payment.AmountMinor, null)),

        PlanChangeOutcome.Scheduled scheduled => Results.Ok(
            new PlanChangeResponse("Scheduled", null, null, null, null, ToResponse(scheduled.Change))),

        PlanChangeOutcome.Invalid invalid => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "That plan change does not make sense.",
            detail: invalid.Reason),

        PlanChangeOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No plan with that id."),

        PlanChangeOutcome.Refused refused => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That plan change cannot go ahead.",
            detail: refused.Reason),

        // 503 rather than 500: nothing was charged, nothing changed, and trying again shortly is
        // the right thing for the agency to do.
        PlanChangeOutcome.GatewayUnavailable unavailable => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The payment provider could not be reached.",
            detail: unavailable.Reason),

        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
    };

    private static MyPlanResponse ToResponse(MySubscriptionView view) =>
        new(
            view.SubscriptionId,
            view.TierId,
            view.PlanName,
            view.Status?.ToString(),
            view.StatusReason,
            view.Currency,
            view.AmountMinor,
            view.Interval?.ToString(),
            view.CurrentPeriodStart,
            view.CurrentPeriodEnd,
            view.TrialEndsAt,
            view.NextChargeAt,
            view.CardOnFile,
            view.DunningRetries,
            view.NextDunningAttemptAt,
            ToFeatures(view.Features),
            view.ScheduledChange is null ? null : ToResponse(view.ScheduledChange));

    private static ScheduledChangeResponse ToResponse(ScheduledChangeView change) =>
        new(
            change.MigrationId,
            change.FromPlan,
            change.ToPlan,
            change.Reason.ToString(),
            change.Explanation,
            change.EffectiveAt,
            change.CanCancel);

    private static SubscriptionInvoiceResponse ToResponse(SubscriptionInvoiceView invoice) =>
        new(
            invoice.Id,
            invoice.InvoiceNumber,
            invoice.ReceiptNumber,
            invoice.Status.ToString(),
            invoice.StatusReason,
            invoice.Currency,
            invoice.TotalMinor,
            invoice.IssuedAt,
            invoice.DueAt,
            invoice.PaidAt,
            invoice.PeriodStart,
            invoice.PeriodEnd,
            [
                .. invoice.Lines.Select(line => new SubscriptionInvoiceLineResponse(
                    line.Description, line.Quantity, line.UnitAmountMinor, line.AmountMinor)),
            ]);

    private static List<PlanFeatureResponse> ToFeatures(IReadOnlyList<PlanFeature> features) =>
        [.. features.Select(feature => new PlanFeatureResponse(feature.Code, feature.Name, feature.Display))];

    private static async ValueTask<object?> RequireAgencyAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        return tenant.HasTenant ? await next(context) : NoAgency();
    }

    private static IResult NoAgency() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Subscriptions belong to travel agencies.",
        detail: "This account is not signed in as an agency, so it has no plan of its own. "
              + "Trips staff manage tiers under the back office instead.");
}
