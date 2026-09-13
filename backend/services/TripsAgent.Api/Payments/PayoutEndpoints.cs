using Microsoft.EntityFrameworkCore;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Api.Payments;

/// <summary>
/// Money out and chargebacks (build plan F12, issue 69): the agency's side under /api/v1, and
/// Finance's side under /api/v1/admin.
/// </summary>
public static class PayoutEndpoints
{
    public static IEndpointRouteBuilder MapPayoutEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapAgencyPayouts(app);
        MapAgencyDisputes(app);
        MapFinance(app);

        return app;
    }

    // ------------------------------------------------------------------------------ agency

    private static void MapAgencyPayouts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/payouts")
            .WithTags("Payouts")
            .RequireAuthorization()
            .AddEndpointFilter(RequireAgencyAsync);

        group.MapGet("/banks", async (BankAccountService service, CancellationToken cancellationToken) =>
            {
                try
                {
                    var banks = await service.BanksAsync(cancellationToken);
                    return Results.Ok(banks.Select(bank => new BankResponse(bank.Code, bank.Name)).ToList());
                }
                catch (PaymentGatewayException)
                {
                    return GatewayDown();
                }
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutRequest))
            .WithName("ListPayoutBanks")
            .Produces<IReadOnlyList<BankResponse>>();

        group.MapGet("/bank-accounts", async (IAppDbContext db, CancellationToken cancellationToken) =>
            {
                // The tenant filter restricts this to the caller's agency.
                var accounts = await db.AgencyBankAccounts
                    .AsNoTracking()
                    .Where(account => account.Status != BankAccountStatus.Removed)
                    .OrderByDescending(account => account.IsDefault)
                    .ThenByDescending(account => account.CreatedAt)
                    .ToListAsync(cancellationToken);

                return Results.Ok(accounts.Select(ToResponse).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutView))
            .WithName("ListBankAccounts")
            .Produces<IReadOnlyList<BankAccountResponse>>();

        group.MapPost("/bank-accounts", async (
                AddBankAccountRequest request,
                BankAccountService service,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await service.AddAsync(
                    request.BankCode ?? string.Empty,
                    request.AccountNumber ?? string.Empty,
                    request.AccountName ?? string.Empty,
                    cancellationToken);

                return outcome switch
                {
                    AddBankAccountOutcome.Added added => Results.Ok(new AddBankAccountResponse(
                        added.BankAccountId, added.AccountName, added.NameMatchesBusiness, added.UsableFrom)),

                    AddBankAccountOutcome.NotResolved notResolved => Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "Your bank does not recognise that account.",
                        detail: notResolved.Reason),

                    AddBankAccountOutcome.Invalid invalid => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest, title: "Check the account details.", detail: invalid.Reason),

                    AddBankAccountOutcome.NotPermitted refused => Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden, title: "Bank accounts cannot be added yet.", detail: refused.Reason),

                    AddBankAccountOutcome.GatewayUnavailable => GatewayDown(),

                    _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutRequest))
            .WithName("AddBankAccount")
            .Produces<AddBankAccountResponse>();

        group.MapPost("/bank-accounts/{bankAccountId:guid}/default", async (
                Guid bankAccountId,
                BankAccountService service,
                CancellationToken cancellationToken) =>
                await service.MakeDefaultAsync(bankAccountId, cancellationToken)
                    ? Results.NoContent()
                    : Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Only a verified account can be the default."))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutRequest))
            .WithName("MakeBankAccountDefault");

        group.MapDelete("/bank-accounts/{bankAccountId:guid}", async (
                Guid bankAccountId,
                BankAccountService service,
                CancellationToken cancellationToken) =>
                await service.RemoveAsync(bankAccountId, cancellationToken)
                    ? Results.NoContent()
                    : Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "That account cannot be removed.",
                        detail: "It may not exist, or a withdrawal is still on its way to it."))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutRequest))
            .WithName("RemoveBankAccount");

        group.MapGet("/balance", async (PayoutService service, CancellationToken cancellationToken) =>
            {
                var balance = await service.WithdrawableAsync(cancellationToken);

                return balance is null
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "This agency has no wallet yet.")
                    : Results.Ok(new WithdrawableBalanceResponse(
                        balance.BalanceMinor,
                        balance.ReservedMinor,
                        balance.AvailableMinor,
                        balance.PendingSettlementMinor,
                        balance.WithdrawableMinor,
                        PayoutLimits.Minimum.AmountMinor,
                        PayoutLimits.DailyCap.AmountMinor,
                        (int)PayoutLimits.SettlementWindow.TotalDays,
                        balance.Currency));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutView))
            .WithName("GetWithdrawableBalance")
            .Produces<WithdrawableBalanceResponse>();

        group.MapGet("/", async (IAppDbContext db, CancellationToken cancellationToken) =>
                Results.Ok(await PayoutsAsync(db, db.Payouts.AsNoTracking(), cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutView))
            .WithName("ListPayouts")
            .Produces<IReadOnlyList<PayoutResponse>>();

        group.MapPost("/", async (
                RequestPayoutRequest request,
                PayoutService service,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await service.RequestAsync(new Money(request.AmountMinor), request.BankAccountId, cancellationToken);

                return outcome switch
                {
                    RequestPayoutOutcome.Requested requested =>
                        Results.Ok(new RequestPayoutResponse(requested.PayoutId, requested.Reference, requested.AmountMinor)),

                    RequestPayoutOutcome.NotAllowed refused => Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity, title: "That withdrawal cannot be made.", detail: refused.Reason),

                    RequestPayoutOutcome.DestinationNotReady notReady => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict, title: "Your bank account is not ready.", detail: notReady.Reason),

                    RequestPayoutOutcome.Busy => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Your balance changed while this was being made.",
                        detail: "Check the available amount and try again."),

                    _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutRequest))
            .WithName("RequestPayout")
            .Produces<RequestPayoutResponse>();
    }

    private static void MapAgencyDisputes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/disputes")
            .WithTags("Disputes")
            .RequireAuthorization()
            .AddEndpointFilter(RequireAgencyAsync);

        group.MapGet("/", async (IAppDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
            {
                // Tenant-filtered: an agency's own chargebacks, soonest deadline first.
                var disputes = await db.Disputes
                    .AsNoTracking()
                    .OrderBy(dispute => dispute.Status == DisputeStatus.Open ? 0 : 1)
                    .ThenBy(dispute => dispute.EvidenceDueAt)
                    .Take(200)
                    .ToListAsync(cancellationToken);

                var now = clock.GetUtcNow();
                return Results.Ok(disputes.Select(dispute => ToResponse(dispute, now, null)).ToList());
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.DisputeView))
            .WithName("ListDisputes")
            .Produces<IReadOnlyList<DisputeResponse>>();

        group.MapGet("/{disputeId:guid}", async (
                Guid disputeId,
                IAppDbContext db,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var dispute = await db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == disputeId, cancellationToken);

                if (dispute is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(ToResponse(dispute, clock.GetUtcNow(), await DefaultsAsync(db, dispute, cancellationToken)));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.DisputeView))
            .WithName("GetDispute")
            .Produces<DisputeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{disputeId:guid}/evidence", async (
                Guid disputeId,
                SubmitDisputeEvidenceRequest request,
                DisputeService service,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                var outcome = await service.SubmitEvidenceAsync(
                    disputeId,
                    new EvidenceSubmission(
                        request.CustomerName ?? string.Empty,
                        request.CustomerEmail ?? string.Empty,
                        request.CustomerPhone ?? string.Empty,
                        request.ServiceDetails ?? string.Empty,
                        request.DeliveryDate,
                        request.Note,
                        request.AssetIds),
                    cancellationToken);

                return outcome switch
                {
                    SubmitEvidenceOutcome.Submitted => Results.NoContent(),
                    SubmitEvidenceOutcome.NotFound => Results.NotFound(),
                    SubmitEvidenceOutcome.TooLate => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "The deadline for evidence has passed.",
                        detail: "Evidence can only be filed before the deadline, or while the dispute is still open."),
                    SubmitEvidenceOutcome.Invalid => Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Fill in the customer's name, email and phone, and describe what was sold."),
                    SubmitEvidenceOutcome.GatewayUnavailable => GatewayDown(),
                    _ => throw new InvalidOperationException($"Unhandled outcome {outcome}."),
                };
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.DisputeRespond))
            .WithName("SubmitDisputeEvidence");
    }

    // ------------------------------------------------------------------------------ Finance

    private static void MapFinance(IEndpointRouteBuilder app)
    {
        var payouts = app.MapGroup("/api/v1/admin/payouts")
            .WithTags("Payout approval")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PayoutApprove));

        payouts.MapGet("/", async (IAppDbContext db, IPlatformScope platformScope, CancellationToken cancellationToken) =>
            {
                using var scope = platformScope.Enter("payout approval queue — withdrawals waiting across every agency");

                var rows = await (
                        from payout in db.Payouts.AsNoTracking()
                        join account in db.AgencyBankAccounts.AsNoTracking() on payout.BankAccountId equals account.Id
                        join agency in db.Agencies.AsNoTracking() on payout.AgencyId equals agency.Id
                        where payout.Status == PayoutStatus.Requested
                              || payout.Status == PayoutStatus.Approved
                              || payout.Status == PayoutStatus.Sending
                              || payout.Status == PayoutStatus.OutcomeUnknown
                        orderby payout.RequestedAt
                        select new { payout, account, agency.LegalName })
                    .Take(200)
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows.Select(row => new PayoutApprovalItemResponse(
                    row.payout.Id, row.payout.AgencyId, row.LegalName, row.payout.Reference, row.payout.AmountMinor.AmountMinor,
                    row.payout.Currency, row.payout.Status.ToString(), row.account.BankName, row.account.MaskedNumber,
                    row.account.AccountNameResolved, row.payout.RequestedByUserId, row.payout.RequestedAt)).ToList());
            })
            .WithName("PayoutApprovalQueue")
            .Produces<IReadOnlyList<PayoutApprovalItemResponse>>();

        payouts.MapPost("/{payoutId:guid}/approve", async (
                Guid payoutId,
                PayoutService service,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return await service.ApproveAsync(payoutId, UserId(http), cancellationToken)
                        ? Results.NoContent()
                        : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That withdrawal is not waiting for approval.");
                }
                catch (InvalidOperationException ex)
                {
                    // Approving your own request. Two people is the control.
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "You cannot approve this.", detail: ex.Message);
                }
            })
            .WithName("ApprovePayout");

        payouts.MapPost("/{payoutId:guid}/reject", async (
                Guid payoutId,
                RejectPayoutRequest request,
                PayoutService service,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request?.Reason))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Say why.",
                        detail: "The agency is shown the reason, and a refusal without one is a support call.");
                }

                return await service.RejectAsync(payoutId, UserId(http), request.Reason, cancellationToken)
                    ? Results.NoContent()
                    : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That withdrawal is not waiting for approval.");
            })
            .WithName("RejectPayout");

        var finance = app.MapGroup("/api/v1/admin/finance")
            .WithTags("Finance review")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.FinanceReview));

        finance.MapGet("/disputes", async (
                IAppDbContext db,
                IPlatformScope platformScope,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                using var scope = platformScope.Enter("dispute queue — Finance reads chargebacks across every agency");

                var disputes = await db.Disputes
                    .AsNoTracking()
                    .OrderBy(dispute => dispute.ResolvedAt == null ? 0 : 1)
                    .ThenBy(dispute => dispute.EvidenceDueAt)
                    .Take(200)
                    .ToListAsync(cancellationToken);

                var now = clock.GetUtcNow();
                return Results.Ok(disputes.Select(dispute => ToResponse(dispute, now, null)).ToList());
            })
            .WithName("FinanceDisputeQueue")
            .Produces<IReadOnlyList<DisputeResponse>>();

        finance.MapGet("/reconciliation/runs", async (ReconciliationTriage triage, CancellationToken cancellationToken) =>
                Results.Ok((await triage.RunsAsync(cancellationToken)).Select(run => new ReconciliationRunResponse(
                    run.Id, run.BusinessDate, run.Gateway, run.Status.ToString(), run.RecordsExamined, run.RecordsMatched,
                    run.ExceptionsRaised, run.GatewayGrossMinor.AmountMinor, run.GatewayFeesMinor.AmountMinor,
                    run.GatewayNetMinor.AmountMinor, run.LedgerGrossMinor.AmountMinor, run.DifferenceMinor.AmountMinor,
                    run.Currency, run.StartedAt, run.CompletedAt, run.FailureReason)).ToList()))
            .WithName("ReconciliationRuns")
            .Produces<IReadOnlyList<ReconciliationRunResponse>>();

        finance.MapGet("/reconciliation/exceptions", async (
                bool? includeClosed,
                ReconciliationTriage triage,
                CancellationToken cancellationToken) =>
                Results.Ok((await triage.ExceptionsAsync(includeClosed == true, cancellationToken)).Select(e =>
                    new ReconciliationExceptionResponse(
                        e.Id, e.Check.ToString(), e.Severity.ToString(), e.Status.ToString(), e.Subject, e.Detail,
                        e.ExpectedMinor.AmountMinor, e.ActualMinor.AmountMinor, e.DifferenceMinor.AmountMinor, e.AgencyId,
                        e.ReconciliationRunId, e.TimesSeen, e.DetectedAt, e.ResolvedAt, e.ResolutionNote)).ToList()))
            .WithName("ReconciliationExceptions")
            .Produces<IReadOnlyList<ReconciliationExceptionResponse>>();

        finance.MapPost("/reconciliation/exceptions/{exceptionId:guid}/close", async (
                Guid exceptionId,
                CloseReconciliationExceptionRequest request,
                ReconciliationTriage triage,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                if (request is null
                    || string.IsNullOrWhiteSpace(request.Note)
                    || !Enum.TryParse<ExceptionClosure>(request.Closure, ignoreCase: true, out var closure))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Choose Resolve or WriteOff, and say why.");
                }

                return await triage.CloseAsync(exceptionId, closure, request.Note, UserId(http), cancellationToken)
                    ? Results.NoContent()
                    : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That exception is not open.");
            })
            .WithName("CloseReconciliationException");
    }

    // ------------------------------------------------------------------------------ helpers

    private static BankAccountResponse ToResponse(AgencyBankAccount account) => new(
        account.Id,
        account.BankName,
        account.MaskedNumber,
        account.AccountNameResolved,
        account.AccountNameProvided,
        account.Status.ToString(),
        account.RejectionReason,
        account.IsDefault,
        account.VerifiedAt,
        account.VerifiedAt is { } verified ? verified + PayoutLimits.NewAccountCoolingOff : null,
        account.Currency);

    private static DisputeResponse ToResponse(Dispute dispute, DateTimeOffset now, DisputeEvidenceDefaults? defaults) => new(
        dispute.Id,
        dispute.AgencyId,
        dispute.PaymentReference,
        dispute.OrderId,
        dispute.AmountMinor.AmountMinor,
        dispute.Currency,
        dispute.Category,
        dispute.Reason,
        dispute.Status.ToString(),
        dispute.HoldOutcome.ToString(),
        dispute.OpenedAt,
        dispute.EvidenceDueAt,
        dispute.EvidenceSubmittedAt,
        dispute.ResolvedAt,
        dispute.Resolution,
        dispute.AcceptsEvidence(now),
        defaults);

    /// <summary>Prefills the evidence form from the order's customer, so an agent under a deadline starts with most of it.</summary>
    private static async Task<DisputeEvidenceDefaults?> DefaultsAsync(IAppDbContext db, Dispute dispute, CancellationToken cancellationToken)
    {
        if (dispute.OrderId is not { } orderId)
        {
            return null;
        }

        var order = await db.Orders.AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.OrderNumber, o.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        var customer = order.CustomerId is { } customerId
            ? await db.Customers.AsNoTracking()
                .Where(c => c.Id == customerId)
                .Select(c => new { c.Name, c.Email, c.Phone })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var lines = await db.OrderLines.AsNoTracking()
            .Where(line => line.OrderId == orderId)
            .Select(line => line.TitleSnapshot)
            .ToListAsync(cancellationToken);

        return new DisputeEvidenceDefaults(
            customer?.Name,
            customer?.Email,
            customer?.Phone,
            $"Booking {order.OrderNumber}: {string.Join("; ", lines)}. Paid by the customer and delivered as booked.");
    }

    private static async Task<IReadOnlyList<PayoutResponse>> PayoutsAsync(
        IAppDbContext db,
        IQueryable<Payout> payouts,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from payout in payouts
                join account in db.AgencyBankAccounts.AsNoTracking() on payout.BankAccountId equals account.Id
                orderby payout.RequestedAt descending
                select new { payout, account })
            .Take(100)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new PayoutResponse(
            row.payout.Id, row.payout.Reference, row.payout.AmountMinor.AmountMinor, row.payout.Currency,
            row.payout.Status.ToString(), row.account.BankName, row.account.MaskedNumber, row.payout.RequestedAt,
            row.payout.ApprovedAt, row.payout.CompletedAt, row.payout.RejectionReason, row.payout.FailureReason)).ToList();
    }

    private static Guid UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(TripsClaimTypes.Subject)?.Value, out var id)
            ? id
            : throw new InvalidOperationException("An authorised request reached a Finance endpoint with no subject claim.");

    private static IResult GatewayDown() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "We could not reach the payment provider.",
        detail: "Nothing was changed. Try again in a minute.");

    /// <summary>403 for a caller with no agency, before any handler runs.</summary>
    private static async ValueTask<object?> RequireAgencyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        return tenant.HasTenant
            ? await next(context)
            : Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Payouts and disputes belong to travel agencies.");
    }
}
