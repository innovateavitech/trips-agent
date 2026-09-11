using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Which side of the books an entry falls on.</summary>
public enum LedgerDirection
{
    /// <summary>Value into this account.</summary>
    Debit = 1,

    /// <summary>Value out of this account.</summary>
    Credit = 2,
}

/// <summary>The kinds of account money moves between.</summary>
public enum LedgerAccountType
{
    /// <summary>An agency's prepaid balance. One per agency per currency.</summary>
    AgencyWallet = 1,

    /// <summary>What Trips has earned — markup, fees, commission.</summary>
    PlatformRevenue = 2,

    /// <summary>What we owe the supplier for tickets issued.</summary>
    SupplierPayable = 3,

    /// <summary>What a traveller owes an agency.</summary>
    CustomerReceivable = 4,

    /// <summary>Money in flight at the payment gateway, not yet settled.</summary>
    GatewayClearing = 5,

    /// <summary>VAT collected and owed onward.</summary>
    TaxPayable = 6,

    /// <summary>Money returned.</summary>
    Refunds = 7,
}

/// <summary>
/// An account in the double-entry ledger.
/// </summary>
/// <remarks>
/// Agency-scoped for wallets and receivables; platform-wide — a null agency — for revenue, tax
/// and gateway clearing, which are Trips' own books rather than any one agency's.
/// </remarks>
public sealed class LedgerAccount : Entity, IAuditableEntity
{
    private LedgerAccount()
    {
        Currency = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Creates an account belonging to one agency.</summary>
    public static LedgerAccount ForAgency(Guid agencyId, LedgerAccountType accountType, string currency, string name)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        return new LedgerAccount
        {
            AgencyId = agencyId,
            AccountType = accountType,
            Currency = Normalise(currency),
            Name = name,
        };
    }

    /// <summary>Creates one of Trips' own accounts, which belong to no agency.</summary>
    public static LedgerAccount ForPlatform(LedgerAccountType accountType, string currency, string name) =>
        new()
        {
            AgencyId = null,
            AccountType = accountType,
            Currency = Normalise(currency),
            Name = name,
        };

    /// <summary>Null for Trips' own accounts.</summary>
    public Guid? AgencyId { get; private set; }

    public LedgerAccountType AccountType { get; private set; }

    /// <summary>ISO 4217. An account holds exactly one currency; money never crosses them silently.</summary>
    public string Currency { get; private set; }

    /// <summary>Human-readable, for statements and reconciliation reports.</summary>
    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    private static string Normalise(string currency)
    {
        var normalised = (currency ?? string.Empty).Trim().ToUpperInvariant();

        return normalised.Length == 3
            ? normalised
            : throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
    }
}

/// <summary>
/// One side of one financial event. Append-only.
/// </summary>
/// <remarks>
/// <para>
/// Entries are never updated or deleted — a database trigger refuses both. A correction is a new
/// reversing entry, which is how accounting has always worked and the only way the history stays
/// true. An edited ledger cannot be audited, because there is no way to tell what it said before.
/// </para>
/// <para>
/// Every entry belongs to a <see cref="TransactionGroupId"/>, and the debits and credits within
/// one group must sum equal. That is asserted by a deferred constraint trigger at commit, so an
/// unbalanced transaction cannot be committed at all — not merely detected afterwards.
/// </para>
/// </remarks>
public sealed class LedgerEntry : Entity, IAuditLogged
{
    private LedgerEntry()
    {
        ReferenceType = string.Empty;
        Description = string.Empty;
    }

    internal static LedgerEntry Create(
        Guid transactionGroupId,
        Guid accountId,
        Guid? agencyId,
        LedgerDirection direction,
        Money amount,
        string referenceType,
        Guid? referenceId,
        string description,
        DateTimeOffset occurredAt)
    {
        if (amount.AmountMinor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount.AmountMinor,
                "A ledger entry must be positive. Direction carries the sign — a negative debit is a credit, "
                + "and allowing both spellings makes every balance query ambiguous.");
        }

        return new LedgerEntry
        {
            TransactionGroupId = transactionGroupId,
            AccountId = accountId,
            AgencyId = agencyId,
            Direction = direction,
            AmountMinor = amount,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            Description = description,
            OccurredAt = occurredAt,
        };
    }

    /// <summary>Ties the sides of one financial event together.</summary>
    public Guid TransactionGroupId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>Denormalised from the account so an agency's statement is one index scan.</summary>
    public Guid? AgencyId { get; private set; }

    public LedgerDirection Direction { get; private set; }

    /// <summary>Always positive. <see cref="Direction"/> carries the sign.</summary>
    public Money AmountMinor { get; private set; }

    /// <summary>What caused this — a payment, an order line, a refund.</summary>
    public string ReferenceType { get; private set; }

    public Guid? ReferenceId { get; private set; }

    public string Description { get; private set; }

    /// <summary>When the event happened, which is not always when the row was written.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
}

/// <summary>
/// A balanced set of ledger entries, built before anything touches the database.
/// </summary>
/// <remarks>
/// <para>
/// The database has the final say — a deferred constraint trigger rejects an unbalanced group at
/// commit. This exists so the failure arrives earlier and with a message naming the difference,
/// rather than as a constraint violation at the end of a transaction that has already done other
/// work.
/// </para>
/// <para>
/// Every financial event in the system should be expressed through this, so there is exactly one
/// place that knows what "balanced" means.
/// </para>
/// </remarks>
public sealed class LedgerTransaction
{
    private readonly List<LedgerEntry> _entries = [];
    private readonly DateTimeOffset _occurredAt;
    private readonly string _referenceType;
    private readonly Guid? _referenceId;

    private LedgerTransaction(DateTimeOffset occurredAt, string referenceType, Guid? referenceId)
    {
        _occurredAt = occurredAt;
        _referenceType = referenceType;
        _referenceId = referenceId;
        TransactionGroupId = Guid.CreateVersion7();
    }

    /// <summary>Ties every entry in this transaction together.</summary>
    public Guid TransactionGroupId { get; }

    /// <summary>Starts a transaction describing one financial event.</summary>
    public static LedgerTransaction Begin(DateTimeOffset occurredAt, string referenceType, Guid? referenceId = null) =>
        new(occurredAt, referenceType, referenceId);

    /// <summary>Records value moving into an account.</summary>
    public LedgerTransaction Debit(LedgerAccount account, Money amount, string description)
    {
        ArgumentNullException.ThrowIfNull(account);

        _entries.Add(LedgerEntry.Create(
            TransactionGroupId, account.Id, account.AgencyId, LedgerDirection.Debit,
            amount, _referenceType, _referenceId, description, _occurredAt));

        return this;
    }

    /// <summary>Records value moving out of an account.</summary>
    public LedgerTransaction Credit(LedgerAccount account, Money amount, string description)
    {
        ArgumentNullException.ThrowIfNull(account);

        _entries.Add(LedgerEntry.Create(
            TransactionGroupId, account.Id, account.AgencyId, LedgerDirection.Credit,
            amount, _referenceType, _referenceId, description, _occurredAt));

        return this;
    }

    /// <summary>Total of the debit side.</summary>
    public Money TotalDebits =>
        _entries.Where(e => e.Direction == LedgerDirection.Debit)
            .Aggregate(Money.Zero, (running, entry) => running + entry.AmountMinor);

    /// <summary>Total of the credit side.</summary>
    public Money TotalCredits =>
        _entries.Where(e => e.Direction == LedgerDirection.Credit)
            .Aggregate(Money.Zero, (running, entry) => running + entry.AmountMinor);

    /// <summary>True when the two sides are equal.</summary>
    public bool IsBalanced => TotalDebits == TotalCredits;

    /// <summary>
    /// Returns the entries, refusing to hand back an unbalanced set.
    /// </summary>
    public IReadOnlyList<LedgerEntry> Build()
    {
        if (_entries.Count == 0)
        {
            throw new InvalidOperationException("A ledger transaction must have entries.");
        }

        if (!IsBalanced)
        {
            throw new InvalidOperationException(
                $"""
                 This ledger transaction does not balance.

                 Debits:  {TotalDebits}
                 Credits: {TotalCredits}
                 Out by:  {TotalDebits - TotalCredits}

                 Every financial event moves value from somewhere to somewhere else. If one side
                 has no obvious counterpart, it is usually platform revenue, tax payable, or a
                 rounding difference that needs its own entry rather than being dropped.
                 """);
        }

        return _entries;
    }
}
