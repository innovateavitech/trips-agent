using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>
/// <see cref="ISupplierBookingLocks"/> in PostgreSQL: <c>FOR UPDATE</c>, and <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL through the request's <see cref="AppDbContext"/>, so it runs on the same connection and in
/// the same transaction as the caller's other work, and under the same row-level security: the tenant
/// session settings are applied to every command EF sends, these included. A worker reaching across
/// agencies does so inside <c>IPlatformScope</c>, as everywhere else.
/// </para>
/// <para>
/// Statuses are sent as parameters, spelled from the enum, because <c>supplier_bookings.status</c> stores
/// each status by its name (see <c>SupplierBookingConfiguration</c>). A renamed enum member fails here, in
/// a test, rather than silently matching nothing in production.
/// </para>
/// </remarks>
public sealed class SupplierBookingLocks : ISupplierBookingLocks
{
    private const string PriceConfirmed = nameof(SupplierBookingStatus.PriceConfirmed);
    private const string Issuing = nameof(SupplierBookingStatus.Issuing);
    private const string IssueOutcomeUnknown = nameof(SupplierBookingStatus.IssueOutcomeUnknown);
    private const string TicketPending = nameof(SupplierBookingStatus.TicketPending);

    private static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes((int)TicketTimeLimitWarning.FifteenMinutes);
    private static readonly TimeSpan SixtyMinutes = TimeSpan.FromMinutes((int)TicketTimeLimitWarning.SixtyMinutes);

    private readonly AppDbContext _db;

    public SupplierBookingLocks(AppDbContext db) => _db = db;

    public async Task<bool> LockAsync(Guid supplierBookingId, CancellationToken cancellationToken = default)
    {
        RequireTransaction(nameof(LockAsync));

        var locked = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM supplier.supplier_bookings
                WHERE id = {supplierBookingId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        return locked.Count == 1;
    }

    public Task<IReadOnlyList<Guid>> ClaimDueForPollingAsync(
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (leaseUntil <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntil), leaseUntil, "A claim's lease must end after it starts.");
        }

        if (_db.Database.CurrentTransaction is not null)
        {
            return ClaimAsync(now, leaseUntil, batchSize, cancellationToken);
        }

        // Its own short transaction, handed whole to the retry strategy AddInfrastructure switches on —
        // which refuses a hand-opened transaction it cannot replay. The same shape as OutboxDispatcher.
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                var claimed = await ClaimAsync(now, leaseUntil, batchSize, token);

                await transaction.CommitAsync(token);
                return claimed;
            },
            cancellationToken);
    }

    public async Task<Guid?> LockNextExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        RequireTransaction(nameof(LockNextExpiredAsync));

        var next = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM supplier.supplier_bookings
                WHERE status = {PriceConfirmed}
                  AND ticket_time_limit <= {now}
                ORDER BY ticket_time_limit, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return next.Count == 0 ? null : next[0];
    }

    public async Task<Guid?> LockNextDueWarningAsync(
        TicketTimeLimitWarning warning,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(nameof(LockNextDueWarningAsync));

        // The hour warning stops where the quarter-hour one starts, so a booking confirmed with ten
        // minutes to spare gets one warning, not two at once.
        FormattableString sql = warning switch
        {
            TicketTimeLimitWarning.FifteenMinutes => $"""
                SELECT id AS "Value"
                FROM supplier.supplier_bookings
                WHERE status = {PriceConfirmed}
                  AND ticket_time_limit > {now}
                  AND ticket_time_limit <= {now + FifteenMinutes}
                  AND fifteen_minute_warning_sent_at IS NULL
                ORDER BY ticket_time_limit, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
            TicketTimeLimitWarning.SixtyMinutes => $"""
                SELECT id AS "Value"
                FROM supplier.supplier_bookings
                WHERE status = {PriceConfirmed}
                  AND ticket_time_limit > {now + FifteenMinutes}
                  AND ticket_time_limit <= {now + SixtyMinutes}
                  AND sixty_minute_warning_sent_at IS NULL
                ORDER BY ticket_time_limit, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(warning), warning, "Unknown warning."),
        };

        var next = await _db.Database.SqlQuery<Guid>(sql).ToListAsync(cancellationToken);

        return next.Count == 0 ? null : next[0];
    }

    /// <summary>
    /// Takes the due rows with SKIP LOCKED and moves their next poll to the end of the lease, before the
    /// transaction commits — so by the time another Worker can see them, they are no longer due.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ClaimAsync(
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var due = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM supplier.supplier_bookings
                WHERE status IN ({Issuing}, {IssueOutcomeUnknown}, {TicketPending})
                  AND next_poll_at <= {now}
                ORDER BY next_poll_at, id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (due.Count > 0)
        {
            var ids = due.ToArray();

            await _db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE supplier.supplier_bookings
                 SET next_poll_at = {leaseUntil}
                 WHERE id = ANY({ids})
                 """,
                cancellationToken);
        }

        return due;
    }

    private void RequireTransaction(string method)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{method} takes a row lock, which lasts only as long as the transaction around it. Call it inside "
                + "ITransactionRunner.RunAsync; outside one, the lock would end the moment the query did.");
        }
    }
}
