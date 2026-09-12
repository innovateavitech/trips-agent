using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Billing;

/// <summary>
/// An agency's own plan: what it is on, what it could be on, what it has been charged, and how to
/// change any of that.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is about the agency making the request, so the tenant query filter does the
/// work and nothing reads another agency's rows. The one exception is the tier catalogue, which
/// the platform owns; reading it goes through <see cref="IPlatformScope"/>.
/// </para>
/// <para>
/// <b>No card details reach this class or any class it calls.</b> A first payment goes through
/// Paystack's hosted page and comes back as a reusable authorisation, and every renewal after that
/// charges the authorisation. Build-plan decision 18, and the reason we are in PCI SAQ-A.
/// </para>
/// </remarks>
public sealed class SubscriptionService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPlatformScope _platformScope;
    private readonly IEntitlements _entitlements;
    private readonly ISubscriptionNumberAllocator _numbers;
    private readonly IPaymentGateway _gateway;
    private readonly TimeProvider _clock;

    public SubscriptionService(
        IAppDbContext db,
        ITenantContext tenant,
        IPlatformScope platformScope,
        IEntitlements entitlements,
        ISubscriptionNumberAllocator numbers,
        IPaymentGateway gateway,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _platformScope = platformScope;
        _entitlements = entitlements;
        _numbers = numbers;
        _gateway = gateway;
        _clock = clock;
    }

    /// <summary>The plans an agency may choose, priced in its own currency.</summary>
    public async Task<IReadOnlyList<PlanView>> PlansAsync(CancellationToken cancellationToken = default)
    {
        var agencyId = RequireAgency();
        var currency = await CurrencyAsync(agencyId, cancellationToken);
        var now = _clock.GetUtcNow();

        var current = await LiveSubscriptionAsync(agencyId, cancellationToken);
        var catalogue = await EntitlementCatalogueAsync(cancellationToken);

        using var scope = _platformScope.Enter("plan picker — reading the published tiers, which the platform owns");

        var tiers = await _db.SubscriptionTiers
            .Include(tier => tier.Prices)
            .Include(tier => tier.Entitlements)
            .Where(tier => tier.Status == TierStatus.Published)
            .OrderBy(tier => tier.SortOrder)
            .ThenBy(tier => tier.Name)
            .ToListAsync(cancellationToken);

        // An archived tier the agency is still on is shown too, so its own plan screen does not
        // claim it is on nothing. Nobody else sees it.
        if (current is not null && tiers.TrueForAll(tier => tier.Id != current.TierId))
        {
            var mine = await _db.SubscriptionTiers
                .Include(tier => tier.Prices)
                .Include(tier => tier.Entitlements)
                .FirstOrDefaultAsync(tier => tier.Id == current.TierId, cancellationToken);

            if (mine is not null)
            {
                tiers.Insert(0, mine);
            }
        }

        return
        [
            .. tiers.Select(tier =>
            {
                var price = tier.PriceAt(currency, BillingInterval.Monthly, now);

                return new PlanView(
                    tier.Id,
                    tier.Code,
                    tier.Name,
                    tier.CustomerDescription,
                    currency,
                    price?.AmountMinor.AmountMinor,
                    price?.Interval ?? BillingInterval.Monthly,
                    tier.TrialDays,
                    current?.TierId == tier.Id,
                    tier.IsFallback,
                    Features(tier, catalogue));
            }),
        ];
    }

    /// <summary>The agency's own plan.</summary>
    public async Task<MySubscriptionView> MineAsync(CancellationToken cancellationToken = default)
    {
        var agencyId = RequireAgency();

        return await ViewAsync(agencyId, cancellationToken);
    }

    /// <summary>The agency's subscription invoices, newest first, with their lines.</summary>
    public async Task<IReadOnlyList<SubscriptionInvoiceView>> InvoicesAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        RequireAgency();

        var invoices = await _db.SubscriptionInvoices
            .Include(invoice => invoice.Lines)
            .OrderByDescending(invoice => invoice.IssuedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);

        return [.. invoices.Select(ToView)];
    }

    /// <summary>One invoice, or null when the agency has none with that id.</summary>
    public async Task<SubscriptionInvoiceView?> InvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        RequireAgency();

        var invoice = await _db.SubscriptionInvoices
            .Include(candidate => candidate.Lines)
            .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken);

        return invoice is null ? null : ToView(invoice);
    }

    /// <summary>
    /// Puts the agency on <paramref name="tierId"/>, or says what has to happen first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three answers, and which one comes back is the whole of the plan-change rule:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Applied</b> — the tier costs nothing (the free fallback plan), or it offers a trial the
    ///     agency has not used. Nothing to pay, so nothing to pay with.
    ///   </item>
    ///   <item>
    ///     <b>PaymentRequired</b> — an upgrade, or a first paid plan. An invoice is raised and the
    ///     agency goes to Paystack's hosted page. The subscription moves when the payment is
    ///     verified, never before: an unpaid upgrade that granted entitlements would be free.
    ///   </item>
    ///   <item>
    ///     <b>Scheduled</b> — a downgrade. It takes effect at the end of the period the agency has
    ///     already paid for, which is also its advance notice (build-plan decision 15). Nothing it
    ///     has built is removed; the new ceilings only block adding more.
    ///   </item>
    /// </list>
    /// </remarks>
    public async Task<PlanChangeOutcome> ChoosePlanAsync(
        Guid tierId,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        var agencyId = RequireAgency();
        var now = _clock.GetUtcNow();
        var currency = await CurrencyAsync(agencyId, cancellationToken);

        SubscriptionTier? tier;
        TierPrice? price;

        using (var scope = _platformScope.Enter("plan change — reading the chosen tier, which the platform owns"))
        {
            tier = await _db.SubscriptionTiers
                .Include(candidate => candidate.Prices)
                .FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

            if (tier is null)
            {
                return new PlanChangeOutcome.NotFound();
            }

            if (!tier.AcceptsNewSubscribers)
            {
                return new PlanChangeOutcome.Refused(
                    $"'{tier.Name}' is not available to join. Choose one of the plans listed on this screen.");
            }

            price = tier.PriceAt(currency, BillingInterval.Monthly, now);
        }

        if (price is null && !tier.IsFallback)
        {
            return new PlanChangeOutcome.Refused(
                $"'{tier.Name}' has no price in {currency}, so it cannot be charged for. "
                + "Ask support to price it before choosing it.");
        }

        var existing = await LiveSubscriptionAsync(agencyId, cancellationToken);

        if (existing?.TierId == tierId)
        {
            return new PlanChangeOutcome.Invalid("The agency is already on that plan.");
        }

        // Free, so there is nothing to take and nothing to wait for.
        if (price is null || price.AmountMinor.IsZero)
        {
            return await ApplyFreePlanAsync(agencyId, existing, tier, price, currency, now, cancellationToken);
        }

        // A downgrade waits for the end of the period the agency has already paid for. That is both
        // the fair thing — they paid for it — and the thirty days' notice decision 15 asks for,
        // since the MVP bills monthly.
        if (existing is not null && await IsDowngradeAsync(existing, price, cancellationToken))
        {
            return await ScheduleDowngradeAsync(agencyId, existing, tier, now, cancellationToken);
        }

        return await StartCheckoutAsync(agencyId, existing, tier, price, currency, callbackUrl, now, cancellationToken);
    }

    /// <summary>
    /// Finishes a hosted-page payment: verifies it with the gateway, keeps the authorisation, and
    /// puts the agency on the plan it paid for.
    /// </summary>
    /// <remarks>
    /// Only the gateway's own answer moves anything. The browser coming back from Paystack says
    /// what the payer's browser was told, and neither that nor a webhook body is the gateway
    /// answering a question we asked.
    /// </remarks>
    public async Task<PlanChangeOutcome> CompleteCheckoutAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var agencyId = RequireAgency();

        var payment = await _db.PaymentTransactions
            .FirstOrDefaultAsync(candidate => candidate.Reference == reference, cancellationToken);

        if (payment is null || payment.Purpose != PaymentPurpose.Subscription)
        {
            return new PlanChangeOutcome.NotFound();
        }

        var invoice = await _db.SubscriptionInvoices
            .Include(candidate => candidate.Lines)
            .FirstOrDefaultAsync(candidate => candidate.InvoiceNumber == reference, cancellationToken);

        if (invoice is null)
        {
            return new PlanChangeOutcome.NotFound();
        }

        if (invoice.Status == SubscriptionInvoiceStatus.Paid)
        {
            // Already done. The agency refreshed the callback page, or the job got there first.
            return new PlanChangeOutcome.Applied(await ViewAsync(agencyId, cancellationToken));
        }

        GatewayVerification verification;

        try
        {
            verification = await _gateway.VerifyAsync(reference, cancellationToken);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            return new PlanChangeOutcome.GatewayUnavailable(ex.Message);
        }

        var now = _clock.GetUtcNow();

        if (!verification.Succeeded)
        {
            if (verification.Outcome == GatewayPaymentOutcome.Pending)
            {
                return new PlanChangeOutcome.GatewayUnavailable(
                    "The payment has not finished yet. Nothing has been charged twice — come back in a minute.");
            }

            payment.MarkFailed(verification.FailureReason, now);
            invoice.MarkPastDue(verification.FailureReason ?? "The payment did not go through.");

            await _db.SaveChangesAsync(cancellationToken);

            return new PlanChangeOutcome.Refused(
                verification.FailureReason ?? "The payment did not go through. Nothing has been charged.");
        }

        payment.MarkSucceeded(
            verification.AmountMinor, verification.FeeMinor, verification.Currency, verification.GatewayReference, now);

        if (payment.Status == PaymentStatus.UnderReview)
        {
            // Somebody was charged, but for the wrong amount or in the wrong currency. Never treated
            // as paid and never treated as failed — telling an agency a payment failed when their
            // card was debited invites them to pay twice. A person decides.
            invoice.MarkPastDue(payment.FailureReason ?? "The payment did not match the invoice.");

            await _db.SaveChangesAsync(cancellationToken);

            return new PlanChangeOutcome.Refused(
                "The payment did not match the invoice, so nothing has been marked as paid. "
                + "Support has been asked to look at it — do not pay again.");
        }

        await CaptureAuthorizationAsync(agencyId, verification.Authorization, now, cancellationToken);

        invoice.MarkPaid(now, payment.Id, await _numbers.NextReceiptNumberAsync(now, cancellationToken));

        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(candidate => candidate.Id == invoice.SubscriptionId, cancellationToken);

        if (subscription is not null)
        {
            subscription.Renew(invoice.PeriodStart, await PriceOfAsync(subscription.TierPriceId, cancellationToken));

            if (verification.GatewayReference is { Length: > 0 } gatewayReference)
            {
                subscription.LinkToGateway(gatewayReference);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // The plan just changed, so anything already resolved for this agency is stale.
        _entitlements.Forget(agencyId);

        return new PlanChangeOutcome.Applied(await ViewAsync(agencyId, cancellationToken));
    }

    /// <summary>
    /// Cancels a scheduled plan change the agency has changed its mind about.
    /// </summary>
    /// <remarks>
    /// Only a change the agency itself asked for. An admin migration is Trips moving a tier's
    /// subscribers, and an agency cancelling that would put it back on a plan we have retired.
    /// </remarks>
    public async Task<PlanChangeOutcome> CancelScheduledChangeAsync(
        Guid migrationId,
        CancellationToken cancellationToken = default)
    {
        var agencyId = RequireAgency();

        var migration = await _db.SubscriptionMigrations
            .FirstOrDefaultAsync(candidate => candidate.Id == migrationId, cancellationToken);

        if (migration is null)
        {
            return new PlanChangeOutcome.NotFound();
        }

        if (!migration.IsPending)
        {
            return new PlanChangeOutcome.Refused("That change has already happened.");
        }

        if (migration.ChangeReason is not (SubscriptionChangeReason.Downgrade or SubscriptionChangeReason.Upgrade))
        {
            return new PlanChangeOutcome.Refused(
                "That change was made by Trips rather than by this agency, so it cannot be cancelled here. "
                + "Get in touch if it is a problem.");
        }

        migration.Cancel(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        return new PlanChangeOutcome.Applied(await ViewAsync(agencyId, cancellationToken));
    }

    private async Task<PlanChangeOutcome> ApplyFreePlanAsync(
        Guid agencyId,
        Subscription? existing,
        SubscriptionTier tier,
        TierPrice? price,
        string currency,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            _db.Subscriptions.Add(Subscription.Start(agencyId, tier, price, currency, now));
        }
        else
        {
            existing.MoveTo(tier, price, now, $"Moved to the {tier.Name} plan.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _entitlements.Forget(agencyId);

        return new PlanChangeOutcome.Applied(await ViewAsync(agencyId, cancellationToken));
    }

    private async Task<PlanChangeOutcome> ScheduleDowngradeAsync(
        Guid agencyId,
        Subscription existing,
        SubscriptionTier tier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await _db.SubscriptionMigrations
            .FirstOrDefaultAsync(
                migration => migration.SubscriptionId == existing.Id
                          && migration.AppliedAt == null
                          && migration.CancelledAt == null,
                cancellationToken);

        if (pending is not null)
        {
            return new PlanChangeOutcome.Refused(
                "A plan change is already scheduled. Cancel that one first if this is a change of mind.");
        }

        var effective = existing.CurrentPeriodEnd > now ? existing.CurrentPeriodEnd : now;

        var migration = SubscriptionMigration.Schedule(
            agencyId,
            existing.Id,
            existing.TierId,
            tier.Id,
            SubscriptionChangeReason.Downgrade,
            $"You asked to move to the {tier.Name} plan. It takes effect when the period you have already paid for "
            + "ends, so nothing you have set up is affected before then.",
            effective);

        migration.MarkNotified(now);
        _db.SubscriptionMigrations.Add(migration);

        await _db.SaveChangesAsync(cancellationToken);

        var view = await ScheduledChangeAsync(migration, cancellationToken);

        return new PlanChangeOutcome.Scheduled(view);
    }

    private async Task<PlanChangeOutcome> StartCheckoutAsync(
        Guid agencyId,
        Subscription? existing,
        SubscriptionTier tier,
        TierPrice price,
        string currency,
        string callbackUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var email = await BillingEmailAsync(agencyId, cancellationToken);

        if (email is null)
        {
            return new PlanChangeOutcome.Refused(
                "This agency has no owner account to send the payment receipt to. Add one and try again.");
        }

        // The subscription row exists before the payment, so the invoice has something to point at,
        // but it is only moved onto the new tier once the money is verified. A trial, where the tier
        // offers one, starts here and is charged when it ends.
        var subscription = existing;

        if (subscription is null)
        {
            subscription = Subscription.Start(agencyId, tier, price, currency, now);
            _db.Subscriptions.Add(subscription);

            if (subscription.Status == SubscriptionStatus.Trialing)
            {
                await _db.SaveChangesAsync(cancellationToken);
                _entitlements.Forget(agencyId);

                return new PlanChangeOutcome.Applied(await ViewAsync(agencyId, cancellationToken));
            }
        }

        var invoice = await RaiseInvoiceAsync(
            agencyId, subscription, tier, price, currency, now, now, periodStart: now, cancellationToken);

        var payment = PaymentTransaction.Start(
            agencyId, null, PaymentPurpose.Subscription, invoice.TotalMinor, currency, invoice.InvoiceNumber);

        _db.PaymentTransactions.Add(payment);

        GatewayInitialization initialization;

        try
        {
            initialization = await _gateway.InitializeAsync(
                invoice.InvoiceNumber, invoice.TotalMinor, currency, email, callbackUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is PaymentGatewayException or PaymentGatewayUnavailableException)
        {
            // Nothing is saved, so there is no invoice to chase and no half-started subscription.
            return new PlanChangeOutcome.GatewayUnavailable(
                "The payment provider could not be reached. Nothing has been charged — try again shortly.");
        }

        payment.RecordGatewayReference(initialization.GatewayReference);

        await _db.SaveChangesAsync(cancellationToken);

        return new PlanChangeOutcome.PaymentRequired(
            initialization.AuthorizationUrl, invoice.InvoiceNumber, invoice.TotalMinor.AmountMinor);
    }

    /// <summary>Raises the invoice for one period, with the single line that explains it.</summary>
    private async Task<SubscriptionInvoice> RaiseInvoiceAsync(
        Guid agencyId,
        Subscription subscription,
        SubscriptionTier tier,
        TierPrice price,
        string currency,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt,
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        var periodEnd = price.Interval == BillingInterval.Annual ? periodStart.AddYears(1) : periodStart.AddMonths(1);

        var invoice = SubscriptionInvoice.Raise(
            agencyId,
            subscription.Id,
            await _numbers.NextInvoiceNumberAsync(issuedAt, cancellationToken),
            currency,
            periodStart,
            periodEnd,
            issuedAt,
            dueAt);

        invoice.AddLine(
            $"{tier.Name} plan — {periodStart:d MMM yyyy} to {periodEnd.AddDays(-1):d MMM yyyy}",
            1,
            price.AmountMinor);

        _db.SubscriptionInvoices.Add(invoice);

        return invoice;
    }

    /// <summary>Stores the reusable authorisation this payment left behind, replacing whatever was there.</summary>
    private async Task CaptureAuthorizationAsync(
        Guid agencyId,
        GatewayAuthorization? authorization,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (authorization is null)
        {
            return;
        }

        var existing = await _db.PaymentAuthorizations
            .Where(candidate => candidate.RevokedAt == null)
            .ToListAsync(cancellationToken);

        var same = existing.Find(candidate => candidate.AuthorizationCode == authorization.Code);

        if (same is not null)
        {
            same.RecordUse(now);
            return;
        }

        // The card they just paid with is the card we charge next time. Anything else means a
        // renewal quietly using a card the agency has stopped meaning to use.
        foreach (var superseded in existing)
        {
            superseded.Revoke(now);
        }

        _db.PaymentAuthorizations.Add(PaymentAuthorization.Capture(
            agencyId,
            _gateway.Name,
            authorization.Code,
            authorization.Brand,
            authorization.Last4,
            authorization.ExpiryMonth,
            authorization.ExpiryYear,
            authorization.Bank,
            now));
    }

    private async Task<bool> IsDowngradeAsync(Subscription existing, TierPrice chosen, CancellationToken cancellationToken)
    {
        var current = await PriceOfAsync(existing.TierPriceId, cancellationToken);

        // No current price means the free plan, so anything with a price is an upgrade.
        return current is not null && chosen.AmountMinor <= current.AmountMinor;
    }

    private async Task<TierPrice?> PriceOfAsync(Guid? tierPriceId, CancellationToken cancellationToken)
    {
        if (tierPriceId is null)
        {
            return null;
        }

        using var scope = _platformScope.Enter("plan change — reading the price a subscription is on");

        return await _db.TierPrices.FirstOrDefaultAsync(price => price.Id == tierPriceId, cancellationToken);
    }

    private async Task<Subscription?> LiveSubscriptionAsync(Guid agencyId, CancellationToken cancellationToken) =>
        await _db.Subscriptions
            .Where(subscription => subscription.AgencyId == agencyId)
            .Where(subscription => subscription.Status == SubscriptionStatus.Trialing
                                || subscription.Status == SubscriptionStatus.Active
                                || subscription.Status == SubscriptionStatus.PastDue)
            .OrderByDescending(subscription => subscription.CurrentPeriodStart)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<MySubscriptionView> ViewAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var subscription = await LiveSubscriptionAsync(agencyId, cancellationToken);
        var entitlements = await _entitlements.ForAsync(agencyId, cancellationToken);
        var catalogue = await EntitlementNamesAsync(cancellationToken);
        var currency = await CurrencyAsync(agencyId, cancellationToken);

        var features = entitlements.All
            .Select(pair => new PlanFeature(
                pair.Key, catalogue.GetValueOrDefault(pair.Key, pair.Key), pair.Value.ToString()))
            .OrderBy(feature => feature.Code, StringComparer.Ordinal)
            .ToList();

        var card = await _db.PaymentAuthorizations
            .Where(authorization => authorization.IsDefault && authorization.RevokedAt == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
        {
            return new MySubscriptionView(
                null, null, "No plan", null, null, currency, null, null, null, null, null, null,
                card?.Display, 0, null, features, null);
        }

        var price = await PriceOfAsync(subscription.TierPriceId, cancellationToken);
        var tierName = await TierNameAsync(subscription.TierId, cancellationToken);

        var pending = await _db.SubscriptionMigrations
            .FirstOrDefaultAsync(
                migration => migration.SubscriptionId == subscription.Id
                          && migration.AppliedAt == null
                          && migration.CancelledAt == null,
                cancellationToken);

        return new MySubscriptionView(
            subscription.Id,
            subscription.TierId,
            tierName,
            subscription.Status,
            subscription.StatusReason,
            subscription.Currency,
            price?.AmountMinor.AmountMinor,
            price?.Interval,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.TrialEndsAt,
            subscription.IsBillable ? subscription.CurrentPeriodEnd : null,
            card?.Display,
            subscription.DunningRetries,
            subscription.NextDunningAttemptAt,
            features,
            pending is null ? null : await ScheduledChangeAsync(pending, cancellationToken));
    }

    private async Task<ScheduledChangeView> ScheduledChangeAsync(
        SubscriptionMigration migration,
        CancellationToken cancellationToken) =>
        new(
            migration.Id,
            await TierNameAsync(migration.FromTierId, cancellationToken),
            await TierNameAsync(migration.ToTierId, cancellationToken),
            migration.ChangeReason,
            migration.Reason,
            migration.ScheduledFor,
            migration.ChangeReason is SubscriptionChangeReason.Downgrade or SubscriptionChangeReason.Upgrade);

    private async Task<string> TierNameAsync(Guid tierId, CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("plan screen — naming the tier a subscription is on");

        var tier = await _db.SubscriptionTiers.FirstOrDefaultAsync(candidate => candidate.Id == tierId, cancellationToken);

        return tier?.Name ?? "Unknown plan";
    }

    private async Task<Dictionary<string, string>> EntitlementNamesAsync(CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("plan screen — naming the entitlements a tier grants");

        return await _db.Entitlements.ToDictionaryAsync(
            entitlement => entitlement.Code, entitlement => entitlement.Name, StringComparer.Ordinal, cancellationToken);
    }

    private async Task<Dictionary<Guid, Entitlement>> EntitlementCatalogueAsync(CancellationToken cancellationToken)
    {
        using var scope = _platformScope.Enter("plan picker — reading the entitlement catalogue");

        return await _db.Entitlements.ToDictionaryAsync(entitlement => entitlement.Id, cancellationToken);
    }

    private async Task<string> CurrencyAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var currency = await _db.Agencies
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.BaseCurrency)
            .FirstOrDefaultAsync(cancellationToken);

        return currency ?? "NGN";
    }

    private async Task<string?> BillingEmailAsync(Guid agencyId, CancellationToken cancellationToken) =>
        await _db.Users
            .Where(user => user.AgencyId == agencyId && user.Status == Domain.Identity.UserStatus.Active)
            .OrderBy(user => user.CreatedAt)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// What a tier gives, as the plan picker lists it.
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="EntitlementSet"/> rather than read straight off the grants, so
    /// the picker answers exactly what the enforcement point will answer. Two places working out
    /// what a plan includes is how a picker ends up advertising something the runtime refuses.
    /// </remarks>
    private static IReadOnlyList<PlanFeature> Features(
        SubscriptionTier tier,
        IReadOnlyDictionary<Guid, Entitlement> catalogue)
    {
        var grants = tier.Entitlements
            .Where(grant => catalogue.ContainsKey(grant.EntitlementId))
            .Select(grant => KeyValuePair.Create(catalogue[grant.EntitlementId].Code, grant.TypedValue));

        var resolved = EntitlementSet.From(tier.Id, tier.Name, grants);
        var names = catalogue.Values.ToDictionary(entitlement => entitlement.Code, entitlement => entitlement.Name, StringComparer.Ordinal);

        return
        [
            .. EntitlementCatalog.All.Select(definition => new PlanFeature(
                definition.Code,
                names.GetValueOrDefault(definition.Code, definition.Name),
                resolved.Value(definition.Code).ToString())),
        ];
    }

    private static SubscriptionInvoiceView ToView(SubscriptionInvoice invoice) =>
        new(
            invoice.Id,
            invoice.InvoiceNumber,
            invoice.ReceiptNumber,
            invoice.Status,
            invoice.StatusReason,
            invoice.Currency,
            invoice.TotalMinor.AmountMinor,
            invoice.IssuedAt,
            invoice.DueAt,
            invoice.PaidAt,
            invoice.PeriodStart,
            invoice.PeriodEnd,
            [
                .. invoice.Lines.Select(line => new SubscriptionInvoiceLineView(
                    line.Description, line.Quantity, line.UnitAmountMinor.AmountMinor, line.AmountMinor.AmountMinor)),
            ]);

    private Guid RequireAgency() =>
        _tenant.AgencyId ?? throw new InvalidOperationException(
            "A subscription belongs to an agency, and none is resolved for this request.");
}
