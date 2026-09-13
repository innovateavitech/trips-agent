using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Payments;

/// <summary>What happened when an agent added a bank account.</summary>
public abstract record AddBankAccountOutcome
{
    private AddBankAccountOutcome()
    {
    }

    /// <summary>The bank named the account and it is ready — after its cooling-off period.</summary>
    /// <param name="BankAccountId">The new account.</param>
    /// <param name="AccountName">What the bank called it. Not what was typed.</param>
    /// <param name="NameMatchesBusiness">
    /// False when the bank's name looks nothing like the agency's. Not a refusal; a flag.
    /// </param>
    /// <param name="UsableFrom">When the cooling-off period ends.</param>
    public sealed record Added(
        Guid BankAccountId,
        string AccountName,
        bool NameMatchesBusiness,
        DateTimeOffset UsableFrom) : AddBankAccountOutcome;

    /// <summary>The bank does not have that account. Almost always a mistyped digit.</summary>
    public sealed record NotResolved(Guid BankAccountId, string Reason) : AddBankAccountOutcome;

    /// <summary>The request itself was wrong — a bad NUBAN, an unknown bank, a duplicate.</summary>
    public sealed record Invalid(string Reason) : AddBankAccountOutcome;

    /// <summary>The agency may not do this yet: unverified, suspended, or a currency with no rail.</summary>
    public sealed record NotPermitted(string Reason) : AddBankAccountOutcome;

    /// <summary>The gateway could not be reached. Nothing was recorded as verified.</summary>
    public sealed record GatewayUnavailable : AddBankAccountOutcome;
}

/// <summary>
/// Capturing where an agency is paid, and proving it before anything is sent there.
/// </summary>
/// <remarks>
/// <para>
/// Capturing an account number is a form. Everything else in this class is what stops that form
/// being a fraud vector: an employee substituting their own account, a transposed digit sending a
/// settlement to a stranger, or somebody who has taken over an agent's login quietly changing the
/// destination and waiting for the next payout.
/// </para>
/// <para>
/// Four controls, and none of them is the name the agent typed:
/// </para>
/// <list type="number">
///   <item>The number is resolved with the bank, and <b>the bank's name is what is stored</b>.</item>
///   <item>
///     That name is compared with the agency's registered name. A mismatch does not refuse —
///     trading names differ from registered ones all the time — it is flagged for review.
///   </item>
///   <item>A new account cannot receive money for 24 hours.</item>
///   <item>The agency's owner is emailed the moment an account is added or changed.</item>
/// </list>
/// </remarks>
public sealed class BankAccountService
{
    private readonly IAppDbContext _db;
    private readonly IBankTransfers _transfers;
    private readonly ITenantContext _tenant;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;

    public BankAccountService(
        IAppDbContext db,
        IBankTransfers transfers,
        ITenantContext tenant,
        INotifier notifier,
        TimeProvider clock)
    {
        _db = db;
        _transfers = transfers;
        _tenant = tenant;
        _notifier = notifier;
        _clock = clock;
    }

    /// <summary>
    /// The banks money can be sent to.
    /// </summary>
    /// <remarks>
    /// Fetched from the gateway every time rather than cached or checked into the repository.
    /// Nigerian banks merge, rebrand and are licensed every year, and this is called when somebody
    /// opens the add-an-account form — a handful of times a day. A cache here would be a second
    /// source of truth for a list whose only job is to be current.
    /// </remarks>
    public Task<IReadOnlyList<BankListing>> BanksAsync(CancellationToken cancellationToken = default) =>
        _transfers.ListBanksAsync(cancellationToken);

    /// <summary>Captures an account and resolves it with the bank in one step.</summary>
    public async Task<AddBankAccountOutcome> AddAsync(
        string bankCode,
        string accountNumber,
        string accountNameProvided,
        CancellationToken cancellationToken = default)
    {
        if (_tenant.AgencyId is not { } agencyId)
        {
            return new AddBankAccountOutcome.NotPermitted("Only an agency can add a bank account.");
        }

        var agency = await _db.Agencies.FirstOrDefaultAsync(a => a.Id == agencyId, cancellationToken);

        if (agency is null || !agency.CanTransact)
        {
            return new AddBankAccountOutcome.NotPermitted(
                "This agency cannot be paid out until its business verification is approved.");
        }

        if (!PayoutLimits.PayableCurrencies.Contains(agency.BaseCurrency))
        {
            // Better a refusal that says so than a balance with no way out. See the epic's
            // finding 6 and open question 17.
            return new AddBankAccountOutcome.NotPermitted(
                $"There is no payout rail for {agency.BaseCurrency}. Bank accounts can only be added for "
                + string.Join(", ", PayoutLimits.PayableCurrencies) + ".");
        }

        var banks = await _transfers.ListBanksAsync(cancellationToken);
        var bank = banks.FirstOrDefault(candidate => candidate.Code == bankCode);

        if (bank is null)
        {
            return new AddBankAccountOutcome.Invalid(
                "That is not a bank we can send money to. Choose one from the list.");
        }

        AgencyBankAccount account;

        try
        {
            account = AgencyBankAccount.Capture(
                agencyId, bank.Code, bank.Name, accountNumber, accountNameProvided, agency.BaseCurrency, _tenant.UserId);
        }
        catch (ArgumentException ex)
        {
            return new AddBankAccountOutcome.Invalid(ex.Message);
        }

        // Compared in memory, not in the WHERE clause: the account number is encrypted with a fresh nonce
        // every time (issue 104), so the database cannot tell two equal numbers apart — and a query
        // comparing it would silently match nothing. An agency has a handful of accounts at most.
        var sameBank = await _db.AgencyBankAccounts
            .Where(existing => existing.BankCode == account.BankCode && existing.Status != BankAccountStatus.Removed)
            .ToListAsync(cancellationToken);

        var duplicate = sameBank.Any(existing => existing.AccountNumber == account.AccountNumber);

        if (duplicate)
        {
            return new AddBankAccountOutcome.Invalid("That account has already been added.");
        }

        // The account is recorded before the bank is asked, so an answer that never arrives leaves
        // a row somebody can see and retry rather than nothing at all.
        _db.AgencyBankAccounts.Add(account);

        ResolvedBankAccount resolved;

        try
        {
            resolved = await _transfers.ResolveAccountAsync(account.AccountNumber, account.BankCode, cancellationToken);
        }
        catch (PaymentGatewayUnavailableException)
        {
            // Nothing is marked verified on a gateway we could not reach. The row stays pending
            // and the agent can ask again.
            await _db.SaveChangesAsync(cancellationToken);
            return new AddBankAccountOutcome.GatewayUnavailable();
        }

        if (!resolved.Resolved)
        {
            account.MarkRejected(
                resolved.FailureReason is { Length: > 0 } reason
                    ? $"The bank could not find that account: {reason}"
                    : "The bank could not find that account.");

            await _db.SaveChangesAsync(cancellationToken);

            return new AddBankAccountOutcome.NotResolved(
                account.Id,
                "Check the account number and the bank. The bank does not recognise this account.");
        }

        string recipientCode;

        try
        {
            recipientCode = await _transfers.CreateRecipientAsync(
                account.AccountNumber, account.BankCode, resolved.AccountName, account.Currency, cancellationToken);
        }
        catch (PaymentGatewayUnavailableException)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new AddBankAccountOutcome.GatewayUnavailable();
        }

        var now = _clock.GetUtcNow();
        account.MarkVerified(resolved.AccountName, recipientCode, now);

        // The first verified account becomes the default, because an agency with exactly one
        // account and no default is a payout form that cannot be submitted for no reason a person
        // could guess.
        var hasDefault = await _db.AgencyBankAccounts.AnyAsync(
            existing => existing.IsDefault && existing.Id != account.Id, cancellationToken);

        if (!hasDefault)
        {
            account.MakeDefault();
        }

        var matches = NameLooksLike(resolved.AccountName, agency);

        await NotifyOwnerAsync(agency, account, matches, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return new AddBankAccountOutcome.Added(
            account.Id, resolved.AccountName, matches, now + PayoutLimits.NewAccountCoolingOff);
    }

    /// <summary>Points future payouts at this account.</summary>
    /// <returns>False when there is no such account, or it is not verified.</returns>
    public async Task<bool> MakeDefaultAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        var account = await _db.AgencyBankAccounts.FirstOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken);

        if (account is null || account.Status != BankAccountStatus.Verified)
        {
            return false;
        }

        // Cleared first: the partial unique index allows exactly one default per agency and
        // currency, so setting the new one before clearing the old would be refused.
        var others = await _db.AgencyBankAccounts
            .Where(other => other.IsDefault && other.Id != bankAccountId && other.Currency == account.Currency)
            .ToListAsync(cancellationToken);

        foreach (var other in others)
        {
            other.ClearDefault();
        }

        account.MakeDefault();
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Retires an account. Never deletes one.</summary>
    /// <returns>False when there is no such account, or a payout is still on its way to it.</returns>
    public async Task<bool> RemoveAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        var account = await _db.AgencyBankAccounts.FirstOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken);

        if (account is null || account.Status == BankAccountStatus.Removed)
        {
            return false;
        }

        var inFlight = await _db.Payouts.AnyAsync(
            payout => payout.BankAccountId == bankAccountId
                      && (payout.Status == PayoutStatus.Requested
                          || payout.Status == PayoutStatus.Approved
                          || payout.Status == PayoutStatus.Sending
                          || payout.Status == PayoutStatus.OutcomeUnknown),
            cancellationToken);

        if (inFlight)
        {
            return false;
        }

        account.Remove();
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Whether the bank's name for the account looks like the agency's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately loose, and deliberately not a gate. "LAGOS TRAVEL LIMITED" against "Lagos
    /// Travel" is the ordinary case, not the suspicious one, and an exact-match rule would refuse
    /// most legitimate agencies while an attacker who had read the KYB record would simply name
    /// the account correctly.
    /// </para>
    /// <para>
    /// What it is for is the flag on the screen and in the owner's email: this account is in a
    /// name that has nothing to do with your business. That is a question worth asking a person,
    /// and a poor thing to refuse a payout over automatically.
    /// </para>
    /// </remarks>
    private static bool NameLooksLike(string accountName, Agency agency)
    {
        var account = Simplify(accountName);

        return Candidates(agency).Any(name => account.Contains(name, StringComparison.Ordinal)
                                              || name.Contains(account, StringComparison.Ordinal));

        static IEnumerable<string> Candidates(Agency agency)
        {
            yield return Simplify(agency.LegalName);

            if (agency.TradingName is { Length: > 0 } trading)
            {
                yield return Simplify(trading);
            }
        }

        // Upper-cased, with punctuation and the company-form words removed, because the bank's
        // record and the CAC's disagree about all three and about none of them meaningfully.
        static string Simplify(string value)
        {
            var letters = new string(value.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
                .ToUpperInvariant();

            var words = letters.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word is not ("LIMITED" or "LTD" or "PLC" or "NIGERIA" or "NIG" or "ENTERPRISES"
                    or "ENTERPRISE" or "VENTURES" or "COMPANY" or "AND" or "THE"));

            return string.Concat(words);
        }
    }

    /// <summary>
    /// Tells the agency's owner that a payout destination changed.
    /// </summary>
    /// <remarks>
    /// To the <b>owner</b>, not to whoever made the change. If the change is an account takeover,
    /// an email to the address the attacker is sitting in front of is not a notification.
    /// </remarks>
    private async Task NotifyOwnerAsync(
        Agency agency,
        AgencyBankAccount account,
        bool nameMatches,
        CancellationToken cancellationToken)
    {
        var owner = await _db.Users
            .AsNoTracking()
            .Where(user => user.AgencyId == agency.Id && user.Status == UserStatus.Active)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName, user.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is null)
        {
            return;
        }

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                agency.Id,
                NotificationTemplateCatalog.PaymentsBankAccountChanged,
                owner.Email,
                $"{owner.FirstName} {owner.LastName}".Trim(),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bankName"] = account.BankName,
                    ["maskedNumber"] = account.MaskedNumber,
                    ["accountName"] = account.AccountNameResolved ?? account.AccountNameProvided,
                    ["nameWarning"] = nameMatches
                        ? string.Empty
                        : "The name on this account does not look like your business name. If you did not expect "
                          + "that, contact support before your next withdrawal.",
                    ["usableFrom"] = (_clock.GetUtcNow() + PayoutLimits.NewAccountCoolingOff)
                        .ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture),
                },
                $"{NotificationTemplateCatalog.PaymentsBankAccountChanged}:{account.Id}",
                owner.Id),
            cancellationToken);
    }
}
