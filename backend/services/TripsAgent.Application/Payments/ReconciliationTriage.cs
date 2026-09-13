using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>How a person closes an exception.</summary>
public enum ExceptionClosure
{
    /// <summary>Put right, and here is how.</summary>
    Resolve = 1,

    /// <summary>Accepted as a loss, and here is why.</summary>
    WriteOff = 2,
}

/// <summary>
/// The Finance side of reconciliation: reading what the reconcilers found, and closing it.
/// </summary>
/// <remarks>
/// <para>
/// Every read and write here crosses agencies, because the queue is the platform's own books, so each
/// goes through <see cref="IPlatformScope"/> — which logs it — and never around the tenant filter.
/// The endpoints in front of this require <c>platform.finance.review</c>.
/// </para>
/// <para>
/// Closing needs a stated reason either way, and there is no bulk close. A button that clears the
/// queue in one click will eventually be used to clear the queue in one click.
/// </para>
/// </remarks>
public sealed class ReconciliationTriage
{
    /// <summary>The most rows one read returns.</summary>
    public const int PageSize = 200;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;

    public ReconciliationTriage(IAppDbContext db, IPlatformScope platformScope, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
    }

    /// <summary>The most recent runs, newest day first.</summary>
    public async Task<IReadOnlyList<ReconciliationRun>> RunsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("reconciliation queue — Finance reads the platform's daily runs");

        return await _db.ReconciliationRuns
            .AsNoTracking()
            .OrderByDescending(run => run.BusinessDate)
            .Take(60)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Exceptions, open ones by default, largest difference first.</summary>
    public async Task<IReadOnlyList<ReconciliationException>> ExceptionsAsync(
        bool includeClosed,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("reconciliation queue — Finance reads exceptions across every agency");

        var query = _db.ReconciliationExceptions.AsNoTracking();

        if (!includeClosed)
        {
            query = query.Where(e => e.Status == ReconciliationStatus.Open || e.Status == ReconciliationStatus.Acknowledged);
        }

        var rows = await query
            .OrderBy(e => e.Severity)
            .ThenBy(e => e.DetectedAt)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        // Sorted by value in memory: the difference is derived, not stored, and a ₦2m exception
        // matters more than forty ₦50 ones.
        return rows
            .OrderBy(e => e.Severity)
            .ThenByDescending(e => Math.Abs(e.DifferenceMinor.AmountMinor))
            .ToList();
    }

    /// <summary>Closes one exception with a reason.</summary>
    /// <returns>False when there is no such exception or it is already closed.</returns>
    public async Task<bool> CloseAsync(
        Guid exceptionId,
        ExceptionClosure closure,
        string note,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        using var scope = _platformScope.Enter("reconciliation queue — Finance closes an exception");

        var exception = await _db.ReconciliationExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId, cancellationToken);

        if (exception is null || exception.Status is ReconciliationStatus.Resolved or ReconciliationStatus.WrittenOff)
        {
            return false;
        }

        if (closure == ExceptionClosure.WriteOff)
        {
            exception.WriteOff(note.Trim(), _clock.GetUtcNow(), userId);
        }
        else
        {
            exception.Resolve(note.Trim(), _clock.GetUtcNow(), userId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
