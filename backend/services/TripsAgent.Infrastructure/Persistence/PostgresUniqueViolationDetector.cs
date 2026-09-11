using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Persistence;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Recognises PostgreSQL's unique-violation error, SQLSTATE 23505.
/// </summary>
/// <remarks>
/// Matches on the error code rather than the message text, which PostgreSQL localises and may
/// reword between versions.
/// </remarks>
public sealed class PostgresUniqueViolationDetector : IUniqueViolationDetector
{
    public bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
