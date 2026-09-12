using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Payments;

/// <summary>Why money went back.</summary>
public enum RefundReason
{
    /// <summary>
    /// The supplier reported a status its reversal rules name — 0, 1 or 11 — so no ticket will come (#43).
    /// Always rests on a recorded status poll.
    /// </summary>
    SupplierReversal = 1,

    /// <summary>An agent chose to refund a failed line from the resolution queue (#44).</summary>
    AgentResolution = 2,
}

/// <summary>How the money went back, which follows from how it was paid.</summary>
public enum RefundMethod
{
    /// <summary>
    /// It was only ever held, never taken: releasing the hold gave it back. No ledger entry, because
    /// no money had moved.
    /// </summary>
    WalletHoldReleased = 1,

    /// <summary>It had been taken from the wallet, and was credited back with reversing ledger entries.</summary>
    WalletCredited = 2,

    /// <summary>Returned through the payment gateway to the card it came from.</summary>
    Gateway = 3,
}

/// <summary>
/// One return of money for an order line: why, how, how much, and the evidence behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per order line</b>, enforced by a unique index: a reversal message delivered twice, or a
/// reversal racing an agent's refund, finds the row already there and refunds nothing more.
/// </para>
/// <para>
/// <b>Evidence first.</b> A supplier reversal cannot be recorded without the status poll that justified
/// it — the factory refuses, and so does a CHECK constraint. Plan §4: automatic reversal fires only on
/// the supplier's documented conditions, and only with a persisted poll behind it.
/// </para>
/// <para>Written once, never edited: a trigger refuses UPDATE and DELETE.</para>
/// </remarks>
public sealed class Refund : Entity, ITenantScoped
{
    private Refund() => Currency = string.Empty;

    public static Refund Record(
        Guid agencyId,
        Guid orderId,
        Guid orderLineId,
        RefundReason reason,
        RefundMethod method,
        Money amount,
        string currency,
        DateTimeOffset refundedAt,
        Guid? supplierBookingId = null,
        Guid? supplierStatusPollId = null,
        Guid? ledgerTransactionGroupId = null,
        Guid? refundedByUserId = null,
        string? note = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (amount.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A refund cannot be negative.");
        }

        if (reason == RefundReason.SupplierReversal && supplierStatusPollId is null)
        {
            throw new ArgumentException(
                "A supplier reversal needs the status poll that justified it. Never reverse without evidence (#43).",
                nameof(supplierStatusPollId));
        }

        if (reason == RefundReason.AgentResolution && refundedByUserId is null)
        {
            throw new ArgumentException("An agent's refund needs the agent who chose it.", nameof(refundedByUserId));
        }

        if (method == RefundMethod.WalletCredited && ledgerTransactionGroupId is null)
        {
            throw new ArgumentException("Money credited back to a wallet moves through the ledger.", nameof(ledgerTransactionGroupId));
        }

        return new Refund
        {
            AgencyId = agencyId,
            OrderId = orderId,
            OrderLineId = orderLineId,
            SupplierBookingId = supplierBookingId,
            SupplierStatusPollId = supplierStatusPollId,
            Reason = reason,
            Method = method,
            AmountMinor = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            LedgerTransactionGroupId = ledgerTransactionGroupId,
            RefundedByUserId = refundedByUserId,
            RefundedAt = refundedAt,
            Note = note is { Length: > 500 } ? note[..500] : note,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Unique: one refund per line.</summary>
    public Guid OrderLineId { get; private set; }

    public Guid? SupplierBookingId { get; private set; }

    /// <summary>The evidence for a supplier reversal. Required for one.</summary>
    public Guid? SupplierStatusPollId { get; private set; }

    public RefundReason Reason { get; private set; }

    public RefundMethod Method { get; private set; }

    public Money AmountMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The reversing ledger entries, when money had moved and had to move back.</summary>
    public Guid? LedgerTransactionGroupId { get; private set; }

    public Guid? RefundedByUserId { get; private set; }

    public DateTimeOffset RefundedAt { get; private set; }

    public string? Note { get; private set; }
}
