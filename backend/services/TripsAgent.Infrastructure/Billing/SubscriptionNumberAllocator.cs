using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Billing;

/// <summary>
/// Allocates subscription invoice and receipt numbers from the two PostgreSQL sequences the
/// billing migration creates.
/// </summary>
/// <remarks>
/// <c>nextval</c> is non-transactional on purpose: it does not block, and a rollback does not give
/// the number back. Both properties are what we want here — no renewal waits behind another, and
/// two invoices can never share a number even if one of their transactions later fails.
/// </remarks>
public sealed class SubscriptionNumberAllocator : ISubscriptionNumberAllocator
{
    private readonly AppDbContext _db;

    public SubscriptionNumberAllocator(AppDbContext db) => _db = db;

    public async Task<string> NextInvoiceNumberAsync(
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default) =>
        Format("INV", issuedAt, await NextAsync("billing.subscription_invoice_number_seq", cancellationToken));

    public async Task<string> NextReceiptNumberAsync(
        DateTimeOffset paidAt,
        CancellationToken cancellationToken = default) =>
        Format("RCT", paidAt, await NextAsync("billing.subscription_receipt_number_seq", cancellationToken));

    private async Task<long> NextAsync(string sequence, CancellationToken cancellationToken)
    {
        // The sequence name goes in as a parameter and is cast to regclass, rather than being
        // pasted into the string. It is a constant from this file either way, but a raw-SQL helper
        // that concatenates is a helper somebody later passes a variable to.
        var next = await _db.Database
            .SqlQuery<long>($"SELECT nextval(CAST({sequence} AS regclass)) AS \"Value\"")
            .ToListAsync(cancellationToken);

        return next[0];
    }

    /// <summary>
    /// <c>TRIPS-INV-2026-000123</c>. The year is the issue year, so the number says when at a glance.
    /// </summary>
    /// <remarks>
    /// The sequence does not reset each year; the year is a label, not part of the counter. Resetting
    /// would mean two invoices called 000001, which is exactly the ambiguity a reference is meant to
    /// remove.
    /// </remarks>
    private static string Format(string kind, DateTimeOffset at, long value) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"TRIPS-{kind}-{at.UtcDateTime.Year:0000}-{value:000000}");
}
