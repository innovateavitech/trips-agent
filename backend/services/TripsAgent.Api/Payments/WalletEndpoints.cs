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
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/wallet")
            .WithTags("Wallet")
            .RequireAuthorization();

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
                IAppDbContext db,
                IPlatformScope platformScope,
                CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(reference, cancellationToken);

                // Read back through the reference rather than trusting the handler's return: it
                // says "did this call credit", and a webhook having beaten us to it is a success
                // for the agent even though this call did nothing.
                using var scope = platformScope.Enter(
                    "top-up status — the payment reference identifies the tenant");

                var payment = await db.PaymentTransactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Reference == reference, cancellationToken);

                if (payment is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "No payment with that reference.");
                }

                var status = payment.Status switch
                {
                    Domain.Payments.PaymentStatus.Succeeded => "succeeded",
                    Domain.Payments.PaymentStatus.Failed => "failed",
                    Domain.Payments.PaymentStatus.Abandoned => "failed",
                    _ => "pending",
                };

                return Results.Ok(new VerifyTopUpResponse(
                    status, payment.Reference, payment.VerifiedAmountMinor?.AmountMinor));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletFund))
            .WithName("VerifyWalletTopUp")
            .Produces<VerifyTopUpResponse>();

        return app;
    }
}
