using Microsoft.EntityFrameworkCore;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Payments;

/// <summary>The wallet, as the agent console uses it: see the balance, add funds.</summary>
/// <remarks>
/// Every route needs a caller with an agency. A Trips super-admin holds the wallet permissions
/// through the all-permissions role but has no wallet, and before this check reached handlers
/// that assumed a tenant and answered 500.
/// </remarks>
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/wallet")
            .WithTags("Wallet")
            .RequireAuthorization()
            .AddEndpointFilter(RequireAgencyAsync);

        group.MapGet("/", async (
                IAppDbContext db,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                // The tenant filter already restricts this to the caller's agency, so there is no
                // agency predicate to get wrong here.
                var wallet = await db.Wallets
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);

                if (wallet is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "This agency has no wallet yet.",
                        detail: "A wallet is created when KYB is approved.");
                }

                return Results.Ok(new WalletBalanceResponse(
                    wallet.BalanceMinor.AmountMinor,
                    wallet.ReservedMinor.AmountMinor,
                    wallet.AvailableMinor.AmountMinor,
                    wallet.Currency));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletView))
            .WithName("GetWalletBalance")
            .Produces<WalletBalanceResponse>();

        // Served so the console can reject a bad amount before anyone is sent to a payment page.
        // The server enforces the same limits regardless.
        group.MapGet("/top-ups/limits", () => Results.Ok(new TopUpLimitsResponse(
                TopUpLimits.Minimum.AmountMinor,
                TopUpLimits.Maximum.AmountMinor,
                "NGN")))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletView))
            .WithName("GetTopUpLimits")
            .Produces<TopUpLimitsResponse>();

        group.MapPost("/top-ups", async (
                StartTopUpRequest request,
                StartTopUpHandler handler,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                if (request.AmountMinor <= 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Say how much to add.",
                        detail: "amountMinor must be a positive number of kobo.");
                }

                var outcome = await handler.HandleAsync(new Money(request.AmountMinor), cancellationToken);

                return outcome switch
                {
                    StartTopUpOutcome.Started started =>
                        Results.Ok(new StartTopUpResponse(started.AuthorizationUrl, started.Reference)),

                    // 403, not 400: the request was fine, the agency is not allowed to fund yet.
                    StartTopUpOutcome.NotPermitted notPermitted => Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "This agency cannot add funds yet.",
                        detail: notPermitted.Reason),

                    StartTopUpOutcome.AmountOutOfRange outOfRange => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "That amount is outside the limits.",
                        detail: outOfRange.Reason),

                    // 502: it is the gateway that failed, not the caller. Nothing was charged, so
                    // the console can invite them to try again honestly.
                    StartTopUpOutcome.GatewayUnavailable => Results.Problem(
                        statusCode: StatusCodes.Status502BadGateway,
                        title: "The payment provider is not responding.",
                        detail: "Nothing has been charged. Please try again in a moment."),

                    _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletFund))
            .WithName("StartWalletTopUp")
            .Produces<StartTopUpResponse>();

        // Where the console lands the agent after the payment page. It is a prompt to go and ask
        // the gateway, not a report of what happened — the answer comes from VerifyAsync.
        group.MapPost("/top-ups/{reference}/verify", async (
                string reference,
                VerifyTopUpHandler handler,
                CancellationToken cancellationToken) =>
            {
                var check = await handler.CheckForAgentAsync(reference, cancellationToken);

                return check switch
                {
                    AgentTopUpCheck.Found found =>
                        Results.Ok(new VerifyTopUpResponse(found.Status, found.Reference, found.AmountMinor)),

                    // The same answer whether the reference does not exist or belongs to another
                    // agency, so the response cannot be used to probe for other agencies' payments.
                    AgentTopUpCheck.NotFound => Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "No payment with that reference."),

                    AgentTopUpCheck.NoAgency => NoAgency(),

                    _ => throw new InvalidOperationException($"Unhandled outcome {check.GetType().Name}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletFund))
            .WithName("VerifyWalletTopUp")
            .Produces<VerifyTopUpResponse>();

        return app;
    }

    /// <summary>403 for a caller with no agency, before any handler runs.</summary>
    private static async ValueTask<object?> RequireAgencyAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        return tenant.HasTenant ? await next(context) : NoAgency();
    }

    private static IResult NoAgency() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Wallets belong to travel agencies.",
        detail: StartTopUpHandler.NoAgencyReason);
}
