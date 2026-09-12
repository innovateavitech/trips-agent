using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>One row of the permissions matrix.</summary>
/// <param name="Code">The permission code, e.g. <c>margin.view</c>.</param>
/// <param name="Category">Which group it belongs to on the matrix.</param>
/// <param name="Description">The plain-English line shown next to the switch.</param>
/// <param name="IsDenied">True when the principal has taken it away.</param>
/// <param name="Reason">Why it was taken away, or null when it has not been.</param>
public sealed record SubAgentPermissionRow(
    string Code,
    string Category,
    string Description,
    bool IsDenied,
    string? Reason);

/// <summary>
/// What a principal has taken away from one of its sub-agents.
/// </summary>
/// <remarks>
/// <para>
/// <b>No cache, on purpose.</b> The epic asks that removing a permission stop working within one
/// request rather than when the access token expires. A cache with an invalidation step would meet
/// that only if every write path remembers to invalidate; one indexed query on a table with a
/// handful of rows per sub-agent meets it by construction, and only sub-agent requests pay for it.
/// If it ever shows up in a trace, the fix is the same Redis-with-a-generation-number pattern
/// <c>IMarkupRuleCache</c> already uses — not a cache without invalidation.
/// </para>
/// <para>
/// <b>Deny only.</b> A principal narrows what its sub-agent's roles already give and can never
/// widen it — see <see cref="PermissionOverride"/>.
/// </para>
/// </remarks>
public sealed class SubAgentPermissionService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;

    public SubAgentPermissionService(IAppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>
    /// The codes denied to <paramref name="agencyId"/>, or an empty list when it is a principal.
    /// </summary>
    /// <remarks>
    /// Read on every authenticated request for a sub-agent user, by the claims transformation that
    /// strips the matching claims off the token before any endpoint's policy is evaluated.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DeniedCodesAsync(
        Guid agencyId,
        CancellationToken cancellationToken = default) =>
        await _db.PermissionOverrides
            .AsNoTracking()
            .Where(entry => entry.SubAgencyId == agencyId)
            .Select(entry => entry.PermissionCode)
            .ToListAsync(cancellationToken);

    /// <summary>The whole matrix for one sub-agent: every overridable permission, denied or not.</summary>
    public async Task<IReadOnlyList<SubAgentPermissionRow>> MatrixAsync(
        Guid subAgencyId,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);

        var denied = await _db.PermissionOverrides
            .AsNoTracking()
            .Where(entry => entry.AgencyId == principalId && entry.SubAgencyId == subAgencyId)
            .Select(entry => new { entry.PermissionCode, entry.Reason })
            .ToListAsync(cancellationToken);

        return
        [
            .. PermissionCodes.All
                .Where(entry => SubAgentPermissions.CanBeOverridden(entry.Code))
                .Select(entry =>
                {
                    var override_ = denied.Find(candidate => candidate.PermissionCode == entry.Code);

                    return new SubAgentPermissionRow(
                        entry.Code,
                        entry.Category,
                        entry.Description,
                        override_ is not null,
                        override_?.Reason);
                }),
        ];
    }

    /// <summary>
    /// Takes a permission away, or restates why it was taken away.
    /// </summary>
    /// <remarks>
    /// Denying <c>margin.view</c> is how a principal hides net rates and markup: there is no
    /// second switch for margin visibility, so the two can never disagree.
    /// </remarks>
    public async Task DenyAsync(
        Guid subAgencyId,
        string permissionCode,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);
        var code = (permissionCode ?? string.Empty).Trim().ToLowerInvariant();

        if (!SubAgentPermissions.CanBeOverridden(code))
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid,
                $"'{permissionCode}' is not a permission you can take away.",
                "It is either not a permission we ship, or one only Trips staff ever hold.");
        }

        var existing = await _db.PermissionOverrides
            .SingleOrDefaultAsync(
                entry => entry.SubAgencyId == subAgencyId && entry.PermissionCode == code,
                cancellationToken);

        if (existing is null)
        {
            _db.PermissionOverrides.Add(
                PermissionOverride.Deny(principalId, subAgencyId, code, reason));
        }
        else
        {
            existing.Restate(reason);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Gives a permission back — the sub-agent's roles decide again.</summary>
    public async Task AllowAsync(
        Guid subAgencyId,
        string permissionCode,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequireOwnedAsync(subAgencyId, cancellationToken);
        var code = (permissionCode ?? string.Empty).Trim().ToLowerInvariant();

        var existing = await _db.PermissionOverrides
            .SingleOrDefaultAsync(
                entry => entry.AgencyId == principalId
                         && entry.SubAgencyId == subAgencyId
                         && entry.PermissionCode == code,
                cancellationToken);

        if (existing is null)
        {
            return;   // nothing was taken away, so nothing to give back
        }

        _db.PermissionOverrides.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The caller's agency, having checked it is the principal of <paramref name="subAgencyId"/>.
    /// </summary>
    /// <remarks>
    /// The query filter already keeps a principal to its own network, but this asks the question
    /// out loud so the refusal is a 404 with a sentence rather than a save that quietly does
    /// nothing.
    /// </remarks>
    private async Task<Guid> RequireOwnedAsync(Guid subAgencyId, CancellationToken cancellationToken)
    {
        var principalId = _tenant.AgencyId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "This request has no agency, so it has no network.");

        var owned = await _db.Agencies
            .AsNoTracking()
            .AnyAsync(
                agency => agency.Id == subAgencyId && agency.ParentAgencyId == principalId,
                cancellationToken);

        return owned
            ? principalId
            : throw new SubAgentRefusedException(
                SubAgentRefusal.NotFound, "That sub-agent is not one of yours.");
    }
}
