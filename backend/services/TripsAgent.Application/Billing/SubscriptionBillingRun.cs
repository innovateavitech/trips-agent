using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Billing;

/// <inheritdoc cref="ISubscriptionBillingRun"/>
public sealed partial class SubscriptionBillingRun : ISubscriptionBillingRun
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IEntitlements _entitlements;
    private readonly ISubscriptionNumberAllocator _numbers;
    private readonly IRecurringChargeGateway _gateway;
    private readonly INotifier _notifier;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubscriptionBillingRun> _logger;

    public SubscriptionBillingRun(
        IAppDbContext db,
        IPlatformScope platformScope,
        IEntitlements entitlements,
        ISubscriptionNumberAllocator numbers,
        IRecurringChargeGateway gateway,
        INotifier notifier,
        IAuditContext audit,
        TimeProvider clock,
        ILogger<SubscriptionBillingRun> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _entitlements = entitlements;
        _numbers = numbers;
        _gateway = gateway;
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SubscriptionBillingRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var runId = Guid.CreateVersion7();
        var now = _clock.GetUtcNow();
        var errors = new List<string>();

        // The actor is left alone: with no user signed in, IAuditContext already reports
        // AuditActorType.System, which is exactly what a nightly job is.
        using var scope = _platformScope.Enter(
            "subscription billing — the nightly pass over every agency's plan, invoices and dunning");

        LogStarted(_logger, runId, now);

        var migrations = await ApplyDueMigrationsAsync(now, errors, cancellationToken);
        var trials = await EndDueTrialsAsync(now, errors, cancellationToken);
        var renewals = await RenewDuePeriodsAsync(now, errors, cancellationToken);
        var dunning = await RunDunningAsync(now, errors, cancellationToken);

        var result = new SubscriptionBillingRunResult(
            runId,
            migrations,
            trials.Ended,
            renewals.Renewed,
            trials.Charged + renewals.Charged + dunning.Charged,
            trials.Failed + renewals.Failed + dunning.Failed,
            dunning.Downgraded,
            dunning.Suspended,
            errors);

        LogFinished(_logger, runId, result.Charged, result.Failed, result.Downgraded, result.Suspended, errors.Count);

        return result;
    }

    /// <summary>
    /// Applies every scheduled tier change whose day has come.
    /// </summary>
    /// <remarks>
    /// First, before anything is charged. A migration landing today changes which tier this month's
    /// invoice is for, and charging the old plan on the morning of the change is the kind of billing
    /// error that costs more in support time than the invoice was worth.
    /// </remarks>
    private async Task<int> ApplyDueMigrationsAsync(
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var due = await _db.SubscriptionMigrations
            .Where(migration => migration.AppliedAt == null && migration.CancelledAt == null)
            .Where(migration => migration.ScheduledFor <= now)
            .OrderBy(migration => migration.ScheduledFor)
            .ToListAsync(cancellationToken);

        var applied = 0;

        foreach (var migration in due)
        {
            try
            {
                var subscription = await _db.Subscriptions
                    .FirstOrDefaultAsync(candidate => candidate.Id == migration.SubscriptionId, cancellationToken);

                if (subscription is null || !subscription.GrantsEntitlements)
                {
                    // The subscription ended some other way. The change has nothing left to do, and
                    // leaving it pending would make it land on whatever replaces it.
                    migration.Cancel(now);
                    await SaveAsync("subscription.migration_obsolete", cancellationToken);
                    continue;
                }

                var tier = await _db.SubscriptionTiers
                    .Include(candidate => candidate.Prices)
                    .FirstOrDefaultAsync(candidate => candidate.Id == migration.ToTierId, cancellationToken);

                if (tier is null)
                {
                    migration.Cancel(now);
                    await SaveAsync("subscription.migration_target_missing", cancellationToken);
                    continue;
                }

                var price = tier.PriceAt(subscription.Currency, BillingInterval.Monthly, now);

                subscription.MoveTo(tier, price, now, migration.Reason);
                migration.MarkApplied(now);

                await SaveAsync("subscription.migrated", cancellationToken);

                // The plan changed, so every entitlement answer for this agency is stale.
                _entitlements.Forget(subscription.AgencyId);
                applied++;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                errors.Add($"migration {migration.Id}: {ex.Message}");
                _db.ChangeTracker.Clear();
            }
        }

        return applied;
    }

    /// <summary>
    /// Ends trials that have run out, and raises the first real invoice.
    /// </summary>
    /// <remarks>
    /// A trial with a card on file is charged and carries on. A trial with none cannot be charged,
    /// so the invoice is raised, the subscription goes past due, and the dunning schedule gives the
    /// agency a week to pay it on the hosted page before anything is taken away. That is the same
    /// week a failed card gets, which keeps one rule instead of two.
    /// </remarks>
    private async Task<(int Ended, int Charged, int Failed)> EndDueTrialsAsync(
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var due = await _db.Subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.Trialing)
            .Where(subscription => subscription.TrialEndsAt != null && subscription.TrialEndsAt <= now)
            .ToListAsync(cancellationToken);

        var ended = 0;
        var charged = 0;
        var failed = 0;

        foreach (var subscription in due)
        {
            try
            {
                var tier = await TierOfAsync(subscription, cancellationToken);
                var price = tier?.PriceAt(subscription.Currency, BillingInterval.Monthly, now);

                if (tier is null || price is null || price.AmountMinor.IsZero)
                {
                    // Nothing to charge for. The trial simply becomes the plan.
                    subscription.ConvertFromTrial(now, price);
                    await SaveAsync("subscription.trial_converted_free", cancellationToken);
                    _entitlements.Forget(subscription.AgencyId);
                    ended++;
                    continue;
                }

                subscription.ConvertFromTrial(now, price);

                var invoice = await RaiseInvoiceAsync(subscription, tier, price, now, now, cancellationToken);
                await SaveAsync("subscription.trial_ended", cancellationToken);

                ended++;

                if (await ChargeAsync(subscription, invoice, tier, now, cancellationToken))
                {
                    charged++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                errors.Add($"trial {subscription.Id}: {ex.Message}");
                _db.ChangeTracker.Clear();
            }
        }

        return (ended, charged, failed);
    }

    /// <summary>Invoices and charges every subscription whose paid period has run out.</summary>
    private async Task<(int Renewed, int Charged, int Failed)> RenewDuePeriodsAsync(
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var due = await _db.Subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.Active)
            .Where(subscription => subscription.CurrentPeriodEnd <= now)
            .Where(subscription => subscription.TierPriceId != null)
            .ToListAsync(cancellationToken);

        var renewed = 0;
        var charged = 0;
        var failed = 0;

        foreach (var subscription in due)
        {
            try
            {
                var tier = await TierOfAsync(subscription, cancellationToken);

                // The price on the subscription, not the tier's current one. Repricing a tier does
                // not reprice its existing subscribers; moving them is a migration, with notice.
                var price = await _db.TierPrices
                    .FirstOrDefaultAsync(candidate => candidate.Id == subscription.TierPriceId, cancellationToken);

                if (tier is null || price is null || price.AmountMinor.IsZero)
                {
                    subscription.Renew(subscription.CurrentPeriodEnd, price);
                    await SaveAsync("subscription.renewed_free", cancellationToken);
                    continue;
                }

                // An unpaid invoice from last month means dunning already owns this subscription.
                // Raising another would bill twice for a month nobody has paid for once.
                if (await HasUnpaidInvoiceAsync(subscription.Id, cancellationToken))
                {
                    continue;
                }

                var periodStart = subscription.CurrentPeriodEnd;
                var invoice = await RaiseInvoiceAsync(subscription, tier, price, now, periodStart, cancellationToken);

                await SaveAsync("subscription.renewal_invoiced", cancellationToken);
                renewed++;

                if (await ChargeAsync(subscription, invoice, tier, now, cancellationToken))
                {
                    charged++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                errors.Add($"renewal {subscription.Id}: {ex.Message}");
                _db.ChangeTracker.Clear();
            }
        }

        return (renewed, charged, failed);
    }

    /// <summary>
    /// Retries failed charges on the schedule, and acts when the schedule runs out.
    /// </summary>
    /// <remarks>
    /// Days 1, 3, 5 and 7 after the first failure, then the outcome:
    /// <see cref="DunningOutcome.DowngradedToFallback"/> when the platform has a free plan to fall
    /// back to, and <see cref="DunningOutcome.Suspended"/> only when it does not.
    /// </remarks>
    private async Task<(int Charged, int Failed, int Downgraded, int Suspended)> RunDunningAsync(
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var pastDue = await _db.Subscriptions
            .Where(subscription => subscription.Status == SubscriptionStatus.PastDue)
            .Where(subscription => subscription.DunningStartedAt != null)
            .ToListAsync(cancellationToken);

        var charged = 0;
        var failed = 0;
        var downgraded = 0;
        var suspended = 0;

        foreach (var subscription in pastDue)
        {
            try
            {
                var invoice = await OldestUnpaidInvoiceAsync(subscription.Id, cancellationToken);

                if (invoice is null)
                {
                    // Somebody paid it on the hosted page between runs. Nothing to chase.
                    subscription.Renew(subscription.CurrentPeriodStart, await PriceOfAsync(subscription, cancellationToken));
                    await SaveAsync("subscription.dunning_resolved", cancellationToken);
                    _entitlements.Forget(subscription.AgencyId);
                    continue;
                }

                if (subscription.DunningIsExhausted)
                {
                    var outcome = await ConcludeDunningAsync(subscription, invoice, now, cancellationToken);

                    if (outcome == DunningOutcome.DowngradedToFallback)
                    {
                        downgraded++;
                    }
                    else
                    {
                        suspended++;
                    }

                    continue;
                }

                if (subscription.NextDunningAttemptAt is { } next && next > now)
                {
                    // Not due yet. The schedule is measured from the first failure, so this is the
                    // ordinary case on most days.
                    continue;
                }

                var tier = await TierOfAsync(subscription, cancellationToken);

                if (tier is null)
                {
                    continue;
                }

                if (await ChargeAsync(subscription, invoice, tier, now, cancellationToken))
                {
                    charged++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                errors.Add($"dunning {subscription.Id}: {ex.Message}");
                _db.ChangeTracker.Clear();
            }
        }

        return (charged, failed, downgraded, suspended);
    }

    /// <summary>
    /// Takes the money for one invoice, and records what happened either way.
    /// </summary>
    /// <returns>True when the money arrived.</returns>
    /// <remarks>
    /// <para>
    /// The attempt's reference is <c>{invoice number}-A{attempt}</c>, which makes it unique per
    /// attempt and idempotent per attempt: Paystack refuses a reference it has already charged, so
    /// running the job twice on the same day cannot charge the same card twice, while tomorrow's
    /// scheduled retry still goes through.
    /// </para>
    /// <para>
    /// An unreachable gateway is <see cref="ChargeAttemptOutcome.Unknown"/> and does <b>not</b> count
    /// against the dunning schedule. A payment provider having a bad afternoon is not the agency
    /// failing to pay, and spending a retry on it would shorten the week they were promised.
    /// </para>
    /// </remarks>
    private async Task<bool> ChargeAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        SubscriptionTier tier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attemptNumber = await _db.SubscriptionChargeAttempts
            .Where(attempt => attempt.InvoiceId == invoice.Id)
            .CountAsync(cancellationToken);

        var reference = string.Create(CultureInfo.InvariantCulture, $"{invoice.InvoiceNumber}-A{attemptNumber}");

        var authorization = await _db.PaymentAuthorizations
            .Where(candidate => candidate.AgencyId == subscription.AgencyId)
            .Where(candidate => candidate.IsDefault && candidate.RevokedAt == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (authorization is null)
        {
            await RecordFailureAsync(
                subscription,
                invoice,
                tier,
                attemptNumber,
                reference,
                ChargeAttemptOutcome.NoAuthorization,
                "There is no card on file to charge.",
                now,
                cancellationToken);

            return false;
        }

        var email = await BillingEmailAsync(subscription.AgencyId, cancellationToken);

        if (email is null)
        {
            await RecordFailureAsync(
                subscription, invoice, tier, attemptNumber, reference, ChargeAttemptOutcome.NoAuthorization,
                "There is no active account on this agency to bill.", now, cancellationToken);

            return false;
        }

        GatewayVerification charge;

        try
        {
            charge = await _gateway.ChargeAsync(
                reference, invoice.TotalMinor, invoice.Currency, email, authorization.AuthorizationCode, cancellationToken);
        }
        catch (Exception ex) when (ex is PaymentGatewayException or PaymentGatewayUnavailableException)
        {
            // Unknown, not failed. The card may well have been charged, so the attempt is recorded
            // and the schedule is left exactly where it was.
            _db.SubscriptionChargeAttempts.Add(SubscriptionChargeAttempt.Record(
                subscription.AgencyId, invoice.Id, attemptNumber, invoice.TotalMinor, reference,
                ChargeAttemptOutcome.Unknown, now, ex.Message, subscription.NextDunningAttemptAt));

            await SaveAsync("subscription.charge_unknown", cancellationToken);

            return false;
        }

        if (charge.Outcome == GatewayPaymentOutcome.Pending)
        {
            _db.SubscriptionChargeAttempts.Add(SubscriptionChargeAttempt.Record(
                subscription.AgencyId, invoice.Id, attemptNumber, invoice.TotalMinor, reference,
                ChargeAttemptOutcome.Unknown, now, charge.FailureReason, subscription.NextDunningAttemptAt));

            await SaveAsync("subscription.charge_pending", cancellationToken);

            return false;
        }

        if (!charge.Succeeded)
        {
            await RecordFailureAsync(
                subscription, invoice, tier, attemptNumber, reference, ChargeAttemptOutcome.Failed,
                charge.FailureReason ?? "The bank declined the payment.", now, cancellationToken);

            return false;
        }

        var payment = PaymentTransaction.Start(
            subscription.AgencyId, null, PaymentPurpose.Subscription, invoice.TotalMinor, invoice.Currency, reference);

        payment.MarkSucceeded(charge.AmountMinor, charge.FeeMinor, charge.Currency, charge.GatewayReference, now);
        _db.PaymentTransactions.Add(payment);

        _db.SubscriptionChargeAttempts.Add(SubscriptionChargeAttempt.Record(
            subscription.AgencyId, invoice.Id, attemptNumber, invoice.TotalMinor, reference,
            ChargeAttemptOutcome.Succeeded, now));

        invoice.MarkPaid(now, payment.Id, await _numbers.NextReceiptNumberAsync(now, cancellationToken));
        authorization.RecordUse(now);

        subscription.Renew(invoice.PeriodStart, await PriceOfAsync(subscription, cancellationToken));

        await QueueReceiptAsync(subscription, invoice, tier, cancellationToken);
        await SaveAsync("subscription.charged", cancellationToken);

        _entitlements.Forget(subscription.AgencyId);

        return true;
    }

    private async Task RecordFailureAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        SubscriptionTier tier,
        int attemptNumber,
        string reference,
        ChargeAttemptOutcome outcome,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nextAttempt = subscription.RecordChargeFailure(now, reason);

        invoice.MarkPastDue(reason);

        _db.SubscriptionChargeAttempts.Add(SubscriptionChargeAttempt.Record(
            subscription.AgencyId, invoice.Id, attemptNumber, invoice.TotalMinor, reference,
            outcome, now, reason, nextAttempt));

        await QueuePaymentFailedAsync(subscription, invoice, tier, reason, nextAttempt, cancellationToken);
        await SaveAsync("subscription.charge_failed", cancellationToken);

        // Nothing about the plan changed — PastDue still grants entitlements — but the screen reads
        // the dunning counters, so the resolved set is no longer what the agency would see.
        _entitlements.Forget(subscription.AgencyId);
    }

    /// <summary>Acts on a subscription whose dunning schedule is spent.</summary>
    private async Task<DunningOutcome> ConcludeDunningAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fallback = await _db.SubscriptionTiers
            .Include(tier => tier.Prices)
            .FirstOrDefaultAsync(tier => tier.IsFallback, cancellationToken);

        invoice.WriteOff("Four attempts over a week were all declined.");

        if (fallback is not null && fallback.Id != subscription.TierId)
        {
            var previousTier = subscription.TierId;
            var price = fallback.PriceAt(subscription.Currency, BillingInterval.Monthly, now);

            subscription.MoveTo(
                fallback, price, now,
                $"Moved to the {fallback.Name} plan because four payment attempts over a week were declined.");

            var migration = SubscriptionMigration.Schedule(
                subscription.AgencyId, subscription.Id, previousTier, fallback.Id,
                SubscriptionChangeReason.DunningFallback,
                "Four payment attempts over a week were declined.", now);

            migration.MarkNotified(now);
            migration.MarkApplied(now);
            _db.SubscriptionMigrations.Add(migration);

            await QueueDunningEndedAsync(
                subscription, invoice, fallback.Name,
                $"Your account has been moved to the {fallback.Name} plan. Everything you have already built is still "
                + "there; you cannot add more than the free plan allows until the bill is settled.",
                cancellationToken);

            await SaveAsync("subscription.downgraded_after_dunning", cancellationToken);
            _entitlements.Forget(subscription.AgencyId);

            return DunningOutcome.DowngradedToFallback;
        }

        // No free plan to fall back to, so the account is suspended. Build-plan decision 14: existing
        // bookings stand, travellers keep their documents, and the storefront goes offline.
        var agency = await _db.Agencies.FirstOrDefaultAsync(
            candidate => candidate.Id == subscription.AgencyId, cancellationToken);

        if (agency is not null && agency.Status != AgencyStatus.Suspended)
        {
            agency.Suspend("Four subscription payment attempts over a week were all declined.", now);
        }

        subscription.Cancel(now, "Four payment attempts over a week were declined and there is no free plan.");

        await QueueDunningEndedAsync(
            subscription, invoice, "none",
            "Your account has been suspended. Bookings already made stand and your travellers keep their documents; "
            + "no new bookings can be taken and your site is offline until the bill is settled.",
            cancellationToken);

        await SaveAsync("subscription.suspended_after_dunning", cancellationToken);
        _entitlements.Forget(subscription.AgencyId);

        return DunningOutcome.Suspended;
    }

    private async Task<SubscriptionInvoice> RaiseInvoiceAsync(
        Subscription subscription,
        SubscriptionTier tier,
        TierPrice price,
        DateTimeOffset issuedAt,
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        var periodEnd = price.Interval == BillingInterval.Annual ? periodStart.AddYears(1) : periodStart.AddMonths(1);

        var invoice = SubscriptionInvoice.Raise(
            subscription.AgencyId,
            subscription.Id,
            await _numbers.NextInvoiceNumberAsync(issuedAt, cancellationToken),
            subscription.Currency,
            periodStart,
            periodEnd,
            issuedAt,
            issuedAt);

        invoice.AddLine(
            $"{tier.Name} plan — {periodStart:d MMM yyyy} to {periodEnd.AddDays(-1):d MMM yyyy}",
            1,
            price.AmountMinor);

        _db.SubscriptionInvoices.Add(invoice);

        return invoice;
    }

    private async Task<bool> HasUnpaidInvoiceAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        await _db.SubscriptionInvoices.AnyAsync(
            invoice => invoice.SubscriptionId == subscriptionId
                    && (invoice.Status == SubscriptionInvoiceStatus.Open
                     || invoice.Status == SubscriptionInvoiceStatus.PastDue),
            cancellationToken);

    private async Task<SubscriptionInvoice?> OldestUnpaidInvoiceAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken) =>
        await _db.SubscriptionInvoices
            .Include(invoice => invoice.Lines)
            .Where(invoice => invoice.SubscriptionId == subscriptionId)
            .Where(invoice => invoice.Status == SubscriptionInvoiceStatus.Open
                           || invoice.Status == SubscriptionInvoiceStatus.PastDue)
            .OrderBy(invoice => invoice.IssuedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<SubscriptionTier?> TierOfAsync(Subscription subscription, CancellationToken cancellationToken) =>
        await _db.SubscriptionTiers
            .Include(tier => tier.Prices)
            .FirstOrDefaultAsync(tier => tier.Id == subscription.TierId, cancellationToken);

    private async Task<TierPrice?> PriceOfAsync(Subscription subscription, CancellationToken cancellationToken) =>
        subscription.TierPriceId is null
            ? null
            : await _db.TierPrices.FirstOrDefaultAsync(
                price => price.Id == subscription.TierPriceId, cancellationToken);

    private async Task<string?> BillingEmailAsync(Guid agencyId, CancellationToken cancellationToken) =>
        await _db.Users
            .Where(user => user.AgencyId == agencyId && user.Status == Domain.Identity.UserStatus.Active)
            .OrderBy(user => user.CreatedAt)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task QueueReceiptAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        SubscriptionTier tier,
        CancellationToken cancellationToken)
    {
        var recipient = await RecipientAsync(subscription.AgencyId, cancellationToken);

        if (recipient is null)
        {
            return;
        }

        await _notifier.QueueEmailAsync(new EmailNotificationRequest(
            subscription.AgencyId,
            NotificationTemplateCatalog.BillingReceipt,
            recipient.Value.Email,
            recipient.Value.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amount"] = FormatMoney(invoice.TotalMinor, invoice.Currency),
                ["planName"] = tier.Name,
                ["receiptNumber"] = invoice.ReceiptNumber ?? string.Empty,
                ["invoiceNumber"] = invoice.InvoiceNumber,
                ["periodStart"] = FormatDate(invoice.PeriodStart),
                ["periodEnd"] = FormatDate(invoice.PeriodEnd.AddDays(-1)),
            },
            $"{NotificationTemplateCatalog.BillingReceipt}:{invoice.Id}",
            recipient.Value.UserId),
            cancellationToken);
    }

    private async Task QueuePaymentFailedAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        SubscriptionTier tier,
        string reason,
        DateTimeOffset? nextAttempt,
        CancellationToken cancellationToken)
    {
        var recipient = await RecipientAsync(subscription.AgencyId, cancellationToken);

        if (recipient is null)
        {
            return;
        }

        await _notifier.QueueEmailAsync(new EmailNotificationRequest(
            subscription.AgencyId,
            NotificationTemplateCatalog.BillingPaymentFailed,
            recipient.Value.Email,
            recipient.Value.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amount"] = FormatMoney(invoice.TotalMinor, invoice.Currency),
                ["planName"] = tier.Name,
                ["invoiceNumber"] = invoice.InvoiceNumber,
                ["reason"] = reason,
                ["nextAttempt"] = nextAttempt is { } next ? FormatDate(next) : "shortly",
            },

            // One per attempt, not one per invoice: four identical emails a week apart is the
            // schedule working, and deduping them would leave the agency told once and downgraded.
            $"{NotificationTemplateCatalog.BillingPaymentFailed}:{invoice.Id}:{subscription.DunningRetries}",
            recipient.Value.UserId),
            cancellationToken);
    }

    private async Task QueueDunningEndedAsync(
        Subscription subscription,
        SubscriptionInvoice invoice,
        string planName,
        string outcome,
        CancellationToken cancellationToken)
    {
        var recipient = await RecipientAsync(subscription.AgencyId, cancellationToken);

        if (recipient is null)
        {
            return;
        }

        await _notifier.QueueEmailAsync(new EmailNotificationRequest(
            subscription.AgencyId,
            NotificationTemplateCatalog.BillingDunningEnded,
            recipient.Value.Email,
            recipient.Value.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["planName"] = planName,
                ["outcome"] = outcome,
                ["invoiceNumber"] = invoice.InvoiceNumber,
                ["amount"] = FormatMoney(invoice.TotalMinor, invoice.Currency),
            },
            $"{NotificationTemplateCatalog.BillingDunningEnded}:{invoice.Id}",
            recipient.Value.UserId),
            cancellationToken);
    }

    private async Task<(Guid UserId, string Email, string Name)?> RecipientAsync(
        Guid agencyId,
        CancellationToken cancellationToken)
    {
        var owner = await _db.Users
            .Where(user => user.AgencyId == agencyId && user.Status == Domain.Identity.UserStatus.Active)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName })
            .FirstOrDefaultAsync(cancellationToken);

        return owner is null ? null : (owner.Id, owner.Email, owner.FirstName);
    }

    private async Task SaveAsync(string action, CancellationToken cancellationToken)
    {
        _audit.SetReason(action);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>"₦25,000.00". Divided by 100 at the very last moment, and never before.</summary>
    private static string FormatMoney(Money amount, string currency) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{currency} {amount.AmountMinor / 100m:N2}");

    private static string FormatDate(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription billing run {RunId} started at {StartedAt}.")]
    private static partial void LogStarted(ILogger logger, Guid runId, DateTimeOffset startedAt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Subscription billing run {RunId} finished: {Charged} charged, {Failed} failed, "
                + "{Downgraded} downgraded, {Suspended} suspended, {Errors} errors.")]
    private static partial void LogFinished(
        ILogger logger, Guid runId, int charged, int failed, int downgraded, int suspended, int errors);
}
