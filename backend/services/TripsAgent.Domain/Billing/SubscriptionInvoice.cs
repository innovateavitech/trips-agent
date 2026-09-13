using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Billing;

/// <summary>
/// What Trips charged one agency for one billing period.
/// </summary>
/// <remarks>
/// <para>
/// Note who is billing whom. Build-plan decision 25 puts the <i>agency</i> on the invoices its
/// travellers receive; this is the other invoice, the one where Trips is the seller and the agency
/// is the customer. It is the only document in the system that carries our name, and CLAUDE.md
/// rule 4 does not apply to it because no traveller ever sees it.
/// </para>
/// <para>
/// <b>The total is the sum of the lines, always.</b> <see cref="TotalMinor"/> is recomputed from
/// the lines every time one is added and can never be set directly, and the migration adds a CHECK
/// and a trigger that say the same thing to anything reaching the table another way. An invoice
/// whose total disagrees with its lines is an invoice nobody can defend in a dispute.
/// </para>
/// </remarks>
public sealed class SubscriptionInvoice : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private readonly List<SubscriptionInvoiceLine> _lines = [];

    private SubscriptionInvoice()
    {
        Currency = string.Empty;
        InvoiceNumber = string.Empty;
    }

    private SubscriptionInvoice(
        Guid agencyId,
        Guid subscriptionId,
        string invoiceNumber,
        string currency,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        AgencyId = agencyId;
        SubscriptionId = subscriptionId;
        InvoiceNumber = invoiceNumber;
        Currency = currency.Trim().ToUpperInvariant();
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        IssuedAt = issuedAt;
        DueAt = dueAt;
        Status = SubscriptionInvoiceStatus.Open;
    }

    /// <summary>Raises an invoice with no lines. Add at least one before it can be charged.</summary>
    public static SubscriptionInvoice Raise(
        Guid agencyId,
        Guid subscriptionId,
        string invoiceNumber,
        string currency,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt) =>
        new(agencyId, subscriptionId, invoiceNumber, currency, periodStart, periodEnd, issuedAt, dueAt);

    public Guid AgencyId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    /// <summary>Ours, not the agency's: <c>TRIPS-INV-2026-000123</c>. Unique across the platform.</summary>
    public string InvoiceNumber { get; private set; }

    /// <summary>Allocated the moment it is paid, and never changed after. Null while unpaid.</summary>
    public string? ReceiptNumber { get; private set; }

    public string Currency { get; private set; }

    public SubscriptionInvoiceStatus Status { get; private set; }

    /// <summary>The sum of the lines, in minor units. Never set directly.</summary>
    public Money TotalMinor { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The payment that settled it, so a receipt can be traced to the money.</summary>
    public Guid? PaymentTransactionId { get; private set; }

    /// <summary>Why it is where it is, for the agency's invoice list.</summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyCollection<SubscriptionInvoiceLine> Lines => _lines;

    /// <summary>True when there is still money to collect on it.</summary>
    public bool IsPayable => Status is SubscriptionInvoiceStatus.Open or SubscriptionInvoiceStatus.PastDue;

    /// <summary>
    /// Adds a line and moves the total with it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The invoice is no longer open for changes.</exception>
    public SubscriptionInvoiceLine AddLine(string description, int quantity, Money unitAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);

        if (Status != SubscriptionInvoiceStatus.Open || PaidAt is not null)
        {
            throw new InvalidOperationException(
                $"Invoice {InvoiceNumber} is {Status} and cannot take another line. "
                + "Raise a credit or a new invoice instead of changing one that has been sent.");
        }

        var line = SubscriptionInvoiceLine.For(AgencyId, Id, description, quantity, unitAmount);
        _lines.Add(line);

        // Recomputed rather than accumulated: an accumulated total drifts the first time a line is
        // ever removed, and the drift is invisible until somebody disputes the bill.
        TotalMinor = Recompute();

        return line;
    }

    /// <summary>The sum of the lines, worked out from scratch.</summary>
    public Money Recompute() =>
        _lines.Aggregate(Money.Zero, (running, line) => running + line.AmountMinor);

    /// <summary>True when the stored total still equals the sum of the lines.</summary>
    public bool AddsUp => TotalMinor == Recompute();

    /// <summary>Marks it paid and allocates its receipt number.</summary>
    public void MarkPaid(DateTimeOffset at, Guid? paymentTransactionId, string receiptNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptNumber);

        if (Status == SubscriptionInvoiceStatus.Paid)
        {
            // Idempotent on purpose: the billing job may be replayed after a crash between the
            // charge and the commit, and a second receipt number for one payment is a reconciliation
            // problem nobody will enjoy.
            return;
        }

        Status = SubscriptionInvoiceStatus.Paid;
        PaidAt = at;
        PaymentTransactionId = paymentTransactionId;
        ReceiptNumber = receiptNumber;
        StatusReason = null;
    }

    /// <summary>Records that a charge against it failed. Still payable; dunning owns it now.</summary>
    public void MarkPastDue(string reason)
    {
        if (Status == SubscriptionInvoiceStatus.Paid)
        {
            return;
        }

        Status = SubscriptionInvoiceStatus.PastDue;
        StatusReason = reason;
    }

    /// <summary>Gives up on collecting it, once dunning is spent.</summary>
    public void WriteOff(string reason)
    {
        if (Status == SubscriptionInvoiceStatus.Paid)
        {
            return;
        }

        Status = SubscriptionInvoiceStatus.Uncollectible;
        StatusReason = reason;
    }

    /// <summary>Cancels an invoice that was superseded before anyone paid it.</summary>
    public void Void(string reason)
    {
        if (Status == SubscriptionInvoiceStatus.Paid)
        {
            throw new InvalidOperationException(
                $"Invoice {InvoiceNumber} has been paid. Refund it; do not void a record of money that moved.");
        }

        Status = SubscriptionInvoiceStatus.Void;
        StatusReason = reason;
    }
}

/// <summary>One charge on a subscription invoice.</summary>
/// <remarks>
/// <c>AmountMinor</c> is stored rather than derived at read time, because the invoice's own total is
/// checked against the sum of these in the database. Two places computing the same product is how a
/// rounding difference of one kobo becomes an invoice that does not add up.
/// </remarks>
public sealed class SubscriptionInvoiceLine : Entity, IAuditableEntity, ITenantScoped
{
    private SubscriptionInvoiceLine() => Description = string.Empty;

    private SubscriptionInvoiceLine(Guid agencyId, Guid invoiceId, string description, int quantity, Money unitAmount)
    {
        AgencyId = agencyId;
        InvoiceId = invoiceId;
        Description = description.Trim();
        Quantity = quantity;
        UnitAmountMinor = unitAmount;
        AmountMinor = unitAmount * quantity;
    }

    internal static SubscriptionInvoiceLine For(
        Guid agencyId, Guid invoiceId, string description, int quantity, Money unitAmount) =>
        new(agencyId, invoiceId, description, quantity, unitAmount);

    public Guid AgencyId { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>"Growth plan — 1 Oct to 31 Oct 2026". Written once, never recalculated.</summary>
    public string Description { get; private set; }

    public int Quantity { get; private set; }

    public Money UnitAmountMinor { get; private set; }

    /// <summary>Quantity times unit price, in minor units.</summary>
    public Money AmountMinor { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
