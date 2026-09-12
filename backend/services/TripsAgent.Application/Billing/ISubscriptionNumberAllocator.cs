namespace TripsAgent.Application.Billing;

/// <summary>
/// Numbers for the invoices and receipts Trips issues to its agencies.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <c>DocumentIssuer</c>. That allocator is gapless within one agency's own
/// sequence, because an agency's invoices to its travellers are its tax documents. These are ours:
/// one platform-wide series, shared by every agency, and an agency never sees another's number.
/// </para>
/// <para>
/// Backed by a PostgreSQL sequence, which means the numbers are unique and ascending but may have
/// gaps — a rolled-back renewal burns a number. That is the right trade here: the alternative is a
/// counter row that serialises every agency's renewal behind every other agency's, at the one time
/// of the month when they all run at once.
/// </para>
/// </remarks>
public interface ISubscriptionNumberAllocator
{
    /// <summary>The next invoice number, e.g. <c>TRIPS-INV-2026-000123</c>.</summary>
    public Task<string> NextInvoiceNumberAsync(DateTimeOffset issuedAt, CancellationToken cancellationToken = default);

    /// <summary>The next receipt number, e.g. <c>TRIPS-RCT-2026-000123</c>.</summary>
    public Task<string> NextReceiptNumberAsync(DateTimeOffset paidAt, CancellationToken cancellationToken = default);
}
