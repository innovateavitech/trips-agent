using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// The row locks the booking pipeline needs, which LINQ cannot express: <c>FOR UPDATE</c>, and
/// <c>FOR UPDATE SKIP LOCKED</c> for work that many Workers share.
/// </summary>
/// <remarks>
/// <para>
/// A port because the SQL lives in Infrastructure, beside the schema it names. Every method answers
/// with ids; the caller then loads the booking through <c>IAppDbContext</c> as usual, while the lock
/// is still held.
/// </para>
/// <para>
/// <b>Why SKIP LOCKED.</b> Two Workers asking for "the next due booking" at once would otherwise both
/// get the same one — and both poll it, or both expire it. With SKIP LOCKED the second is handed the
/// next row instead, so any number of Workers can run the same job safely.
/// </para>
/// </remarks>
public interface ISupplierBookingLocks
{
    /// <summary>
    /// Locks one booking's row until the caller's transaction ends, waiting if someone else holds it.
    /// </summary>
    /// <remarks>Must be called inside a transaction — <c>ITransactionRunner</c> — or the lock ends at once.</remarks>
    /// <returns>False when there is no such booking, or it belongs to another agency.</returns>
    public Task<bool> LockAsync(Guid supplierBookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> bookings awaiting an outcome whose next poll is due,
    /// by moving their next poll to <paramref name="leaseUntil"/>.
    /// </summary>
    /// <remarks>
    /// Runs in a short transaction of its own, so no lock is held while the supplier is asked. The
    /// lease is what keeps another Worker away until then; if this one dies mid-poll, the lease lapses
    /// and the booking is polled again.
    /// </remarks>
    public Task<IReadOnlyList<Guid>> ClaimDueForPollingAsync(
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the next held booking — price confirmed, never issued — whose ticket time limit has
    /// passed, skipping any another Worker holds. Inside a transaction.
    /// </summary>
    public Task<Guid?> LockNextExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the next held booking that is due <paramref name="warning"/> and has not had it,
    /// skipping any another Worker holds. Inside a transaction.
    /// </summary>
    public Task<Guid?> LockNextDueWarningAsync(
        TicketTimeLimitWarning warning,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
