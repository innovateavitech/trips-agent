using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Tenancy;

/// <summary>
/// Moves a sub-agent's allowance through the two SECURITY DEFINER functions the
/// <c>AddSubAgentNetwork</c> migration installs.
/// </summary>
/// <remarks>
/// <para>
/// Both are single statements against one row, and both run on the caller's own connection — so
/// when the caller has a transaction open (the checkout does), the reservation commits or rolls
/// back with the booking rather than on its own.
/// </para>
/// <para>
/// The functions are the reason this is not plain SQL here: the allowance row belongs to the
/// principal, and a sub-agent's session cannot write it. A definer-rights function that can only
/// add to <c>spent_minor</c>, only within the limit, and only on the calling agency's own row is a
/// far smaller hole than an UPDATE policy that would let it write any column.
/// </para>
/// </remarks>
public sealed class PostgresAllowanceReservations : IAllowanceReservations
{
    private readonly AppDbContext _db;

    public PostgresAllowanceReservations(AppDbContext db) => _db = db;

    public async Task<AllowanceReservation> ReserveAsync(
        string currency,
        long amountMinor,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ScalarAsync(
            "SELECT payments.reserve_sub_agent_allowance(@currency, @amount)",
            cancellationToken,
            new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = Normalise(currency) },
            new NpgsqlParameter("amount", NpgsqlDbType.Bigint) { Value = amountMinor });

        return outcome switch
        {
            "ok" => AllowanceReservation.Reserved,
            "no_allowance" => AllowanceReservation.NoAllowance,
            "frozen" => AllowanceReservation.Frozen,
            "exceeded" => AllowanceReservation.Exceeded,
            "no_tenant" => AllowanceReservation.NoTenant,
            _ => throw new InvalidOperationException(
                $"payments.reserve_sub_agent_allowance returned '{outcome}', which this build does not know. "
                + "The function and this switch have to be changed together."),
        };
    }

    public async Task<bool> ReleaseAsync(
        Guid subAgencyId,
        string currency,
        long amountMinor,
        CancellationToken cancellationToken = default)
    {
        var released = await ScalarAsync(
            "SELECT payments.release_sub_agent_allowance(@subAgency, @currency, @amount)::text",
            cancellationToken,
            new NpgsqlParameter("subAgency", NpgsqlDbType.Uuid) { Value = subAgencyId },
            new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = Normalise(currency) },
            new NpgsqlParameter("amount", NpgsqlDbType.Bigint) { Value = amountMinor });

        return string.Equals(released, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string currency) =>
        (currency ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Runs one statement on the context's own connection, joining whatever transaction it has.
    /// </summary>
    /// <remarks>
    /// Through <c>GetDbConnection</c> rather than <c>ExecuteSqlRaw</c> because these return a
    /// value. The connection is the context's, so the session settings row-level security reads —
    /// <c>app.agency_id</c>, written by <c>TenantSessionInterceptor</c> — are already in place.
    /// </remarks>
    private async Task<string?> ScalarAsync(
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        var connection = _db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result as string ?? result?.ToString();
    }
}
