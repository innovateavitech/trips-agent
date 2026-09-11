using Microsoft.EntityFrameworkCore;

namespace TripsAgent.Application.Persistence;

/// <summary>
/// Tells a unique-index violation apart from every other reason a save can fail.
/// </summary>
/// <remarks>
/// <para>
/// Several money paths rely on a unique index to pick one winner among racing writers — the
/// webhook event id, the one-posting-per-payment index on the ledger. The loser's save fails,
/// and that failure is expected and safe to absorb.
/// </para>
/// <para>
/// Every <i>other</i> failure is not. A read-only replica after a failover, a value too long for
/// its column, a dropped connection: absorbing those as "someone else won" silently loses the
/// write. So the check has to be exact, and the exact check needs the database driver's error
/// code — which Application may not reference. This port keeps Npgsql in Infrastructure.
/// </para>
/// </remarks>
public interface IUniqueViolationDetector
{
    /// <summary>True only when <paramref name="exception"/> was caused by a unique index.</summary>
    public bool IsUniqueViolation(DbUpdateException exception);
}
