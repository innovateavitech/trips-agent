using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// The supplier has issued the ticket. The checkout saga (#42) captures the wallet hold and sends
/// the traveller their confirmation.
/// </summary>
/// <param name="SupplierStatusPollId">The poll that learned it, or null when the issue call's own answer said so.</param>
public sealed record BookingTicketed(
    Guid SupplierBookingId,
    Guid AgencyId,
    Guid OrderLineId,
    string? Pnr,
    int? SupplierStatusCode,
    Guid? SupplierStatusPollId,
    DateTimeOffset TicketedAt) : IDomainEvent;

/// <summary>
/// The supplier reported a status after which no ticket will be issued — Trips Africa's 0
/// (Booking), 1 (Cancelled) or 11 (CancellationFailed) — so the money taken for it must go back.
/// Consumed by the payment reversal worker (#43).
/// </summary>
/// <remarks>
/// Only ever raised by a status poll, and it names that poll. The reversal worker must never reverse
/// without a recorded <c>supplier_status_polls</c> row justifying it, and this is the row.
/// </remarks>
/// <param name="HttpStatusCode">The status query's HTTP status.</param>
/// <param name="SupplierStatusPollId">The evidence: the poll that reported the status.</param>
public sealed record PaymentReversalRequired(
    Guid SupplierBookingId,
    Guid AgencyId,
    Guid OrderLineId,
    int SupplierStatusCode,
    int? HttpStatusCode,
    Guid SupplierStatusPollId,
    string Reason,
    DateTimeOffset RequiredAt) : IDomainEvent;

/// <summary>
/// A held booking's ticket time limit passed before issuing began, so the supplier has released it.
/// Nothing was issued. Raised by the ticket time limit monitor (#38).
/// </summary>
public sealed record SupplierBookingExpired(
    Guid SupplierBookingId,
    Guid AgencyId,
    Guid OrderLineId,
    DateTimeOffset TicketTimeLimit,
    DateTimeOffset ExpiredAt) : IDomainEvent;
