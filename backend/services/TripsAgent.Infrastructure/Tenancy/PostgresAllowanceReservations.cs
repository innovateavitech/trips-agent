using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Tenancy;

/// <summary>
/// Moves a sub-agent's allowance through the two SECURITY DEFINER functions the
/// <c>AddSubAgentNetwork</c> migration installs.
/// </summary>
/// <remarks>
/// <para>
/// Both are single statements against one row, run on the context's own connection — so when the
/// caller has a transaction open, as the checkout does, the reservation commits or rolls back with
/// the booking rather than on its own.
/// </para>
/// <para>
/// <b>Through EF's <c>SqlQueryRaw</c> rather than a hand-made command.</b> The row-level security
/// settings a policy reads — <c>app.agency_id</c> and <c>app.platform_scope</c> — are written by
/// <c>TenantSessionInterceptor</c>, which hooks commands EF executes. A command created straight
/// off the connection would skip that, and could run with settings describing a moment that has
/// passed — a platform scope opened since the last query, say. Going through EF means these two
/// statements are policed exactly like every other.
/// </para>
/// <para>
/// The functions are also why this is not a plain UPDATE: the allowance row belongs to the
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
        // The column alias is what EF requires of a scalar SqlQuery: it projects a column named
        // "Value". Everything else here is a parameter, never interpolated.
        var outcome = await _db.Database
            .SqlQueryRaw<string>(
                """SELECT payments.reserve_sub_agent_allowance({0}, {1}) AS "Value" """,
                Normalise(currency),
                amountMinor)
            .SingleAsync(cancellationToken);

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
        CancellationToken cancellationToken = default) =>
        await _db.Database
            .SqlQueryRaw<bool>(
                """SELECT payments.release_sub_agent_allowance({0}, {1}, {2}) AS "Value" """,
                subAgencyId,
                Normalise(currency),
                amountMinor)
            .SingleAsync(cancellationToken);

    private static string Normalise(string currency) =>
        (currency ?? string.Empty).Trim().ToUpperInvariant();
}
