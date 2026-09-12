using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>One sub-agent's allowance, as both sides of the network see it.</summary>
/// <param name="SpentMinor">Reserved this period, in kobo.</param>
/// <param name="LimitMinor">The cap for this period, in kobo.</param>
/// <param name="RemainingMinor">What is left, in kobo. Never negative.</param>
public sealed record AllowanceView(
    Guid SubAgencyId,
    string Currency,
    long SpentMinor,
    long LimitMinor,
    long RemainingMinor,
    AllowancePeriod Period,
    AllowanceStatus Status,
    DateTimeOffset? ResetsAt);

/// <summary>
/// The principal's side of a sub-agent's allowance: opening one, changing the cap, freezing it.
/// </summary>
/// <remarks>
/// Spending it is not here. That is <see cref="IAllowanceReservations"/>, because it has to be a
/// single conditional statement rather than a load-check-save through the change tracker — see the
/// remarks there.
/// </remarks>
public sealed class SubAgentAllowanceService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public SubAgentAllowanceService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>One sub-agent's allowance, or null when it has none and so may not spend.</summary>
    public async Task<AllowanceView?> GetAsync(Guid subAgencyId, CancellationToken cancellationToken = default)
    {
        var allowance = await _db.WalletAllowances
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SubAgencyId == subAgencyId, cancellationToken);

        return allowance is null ? null : ToView(allowance);
    }

    /// <summary>
    /// The caller's own allowance, for a sub-agent looking at what it has left.
    /// </summary>
    /// <remarks>
    /// It reads its own row through the widened SELECT policy — see the migration — and cannot
    /// change it: every write policy on the table is still <c>agency_id = the principal</c>.
    /// </remarks>
    public Task<AllowanceView?> OwnAsync(CancellationToken cancellationToken = default) =>
        _tenant.AgencyId is { } agencyId
            ? GetAsync(agencyId, cancellationToken)
            : Task.FromResult<AllowanceView?>(null);

    /// <summary>
    /// Opens an allowance, or changes the cap and period of one that exists.
    /// </summary>
    /// <remarks>
    /// Lowering the cap below what has already been spent is allowed and takes nothing back: the
    /// money is spent. It stops the next booking, and the period reset puts it right.
    /// </remarks>
    public async Task<AllowanceView> SetAsync(
        Guid subAgencyId,
        long limitMinor,
        AllowancePeriod period,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);
        var now = _clock.GetUtcNow();

        if (limitMinor < 0)
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid,
                "An allowance cannot be negative.",
                "Set it to zero to stop this sub-agent spending anything.");
        }

        var currency = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == principalId)
            .Select(agency => agency.BaseCurrency)
            .SingleAsync(cancellationToken);

        var allowance = await _db.WalletAllowances
            .SingleOrDefaultAsync(candidate => candidate.SubAgencyId == subAgencyId, cancellationToken);

        if (allowance is null)
        {
            allowance = WalletAllowance.Open(
                principalId, subAgencyId, currency, new Money(limitMinor), period, now);

            _db.WalletAllowances.Add(allowance);
        }
        else
        {
            allowance.ChangeLimit(new Money(limitMinor));

            if (allowance.Period != period)
            {
                allowance.ChangePeriod(period, now);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return ToView(allowance);
    }

    /// <summary>Stops anything being reserved against the allowance, without touching the cap.</summary>
    public Task<AllowanceView> FreezeAsync(Guid subAgencyId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(subAgencyId, freeze: true, cancellationToken);

    /// <summary>Lets it be drawn on again.</summary>
    public Task<AllowanceView> UnfreezeAsync(Guid subAgencyId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(subAgencyId, freeze: false, cancellationToken);

    private async Task<AllowanceView> SetStatusAsync(
        Guid subAgencyId,
        bool freeze,
        CancellationToken cancellationToken)
    {
        await RequireOwnedAsync(subAgencyId, cancellationToken);

        var allowance = await _db.WalletAllowances
            .SingleOrDefaultAsync(candidate => candidate.SubAgencyId == subAgencyId, cancellationToken)
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.NotFound,
                "That sub-agent has no allowance.",
                "Set one first — until then it cannot spend anything at all.");

        if (freeze)
        {
            allowance.Freeze();
        }
        else
        {
            allowance.Unfreeze();
        }

        await _db.SaveChangesAsync(cancellationToken);

        return ToView(allowance);
    }

    private static AllowanceView ToView(WalletAllowance allowance) =>
        new(
            allowance.SubAgencyId,
            allowance.Currency,
            allowance.SpentMinor.AmountMinor,
            allowance.LimitMinor.AmountMinor,
            allowance.RemainingMinor.AmountMinor,
            allowance.Period,
            allowance.Status,
            allowance.ResetsAt);

    private async Task<Guid> RequireOwnedAsync(Guid subAgencyId, CancellationToken cancellationToken)
    {
        var principalId = _tenant.AgencyId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "This request has no agency, so it has no network.");

        var owned = await _db.Agencies
            .AsNoTracking()
            .AnyAsync(agency => agency.Id == subAgencyId && agency.ParentAgencyId == principalId, cancellationToken);

        return owned
            ? principalId
            : throw new SubAgentRefusedException(
                SubAgentRefusal.NotFound, "That sub-agent is not one of yours.");
    }
}
