using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>Why a sub-agent request was refused, so the endpoint can choose a status code.</summary>
public enum SubAgentRefusal
{
    /// <summary>The request is wrong — a missing name, a bad email. 400.</summary>
    Invalid = 1,

    /// <summary>The caller is not this sub-agent's principal, or is a sub-agent itself. 403.</summary>
    Forbidden = 2,

    /// <summary>No such sub-agent under this principal. 404.</summary>
    NotFound = 3,

    /// <summary>The plan does not allow another sub-agent. 409.</summary>
    EntitlementExhausted = 4,

    /// <summary>The state does not allow it — already revoked, slug taken. 409.</summary>
    Conflict = 5,
}

/// <summary>A sub-agent request that could not go ahead. Carries what to tell the agent.</summary>
public sealed class SubAgentRefusedException : Exception
{
    public SubAgentRefusedException(SubAgentRefusal refusal, string message, string? detail = null)
        : base(message)
    {
        Refusal = refusal;
        Detail = detail;
    }

    public SubAgentRefusedException()
        : this(SubAgentRefusal.Invalid, "The request could not be completed.")
    {
    }

    public SubAgentRefusedException(string message)
        : this(SubAgentRefusal.Invalid, message)
    {
    }

    public SubAgentRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusal = SubAgentRefusal.Invalid;
    }

    public SubAgentRefusal Refusal { get; }

    /// <summary>The second sentence: what to do about it. Shown under the message.</summary>
    public string? Detail { get; }
}

/// <summary>One sub-agent as the principal's list shows it.</summary>
/// <param name="AllowanceSpentMinor">Reserved this period, in kobo. Null when it has no allowance.</param>
/// <param name="AllowanceLimitMinor">The cap for this period, in kobo. Null when it has no allowance.</param>
public sealed record SubAgentSummary(
    Guid Id,
    string LegalName,
    string? TradingName,
    string Slug,
    AgencyStatus Status,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    bool HasOpenInvitation,
    int ScopeCount,
    int DeniedPermissionCount,
    bool CanSeeMargin,
    long? AllowanceSpentMinor,
    long? AllowanceLimitMinor,
    string? AllowanceCurrency);

/// <summary>What a principal's network looks like, and how much room its plan leaves.</summary>
public sealed record SubAgentNetwork(
    IReadOnlyList<SubAgentSummary> SubAgents,
    int? MaxSubAgents,
    bool CanAddAnother,
    string? CannotAddReason);

/// <summary>A sub-agent that was just created, and the one-time link to hand its owner.</summary>
/// <param name="InvitationToken">
/// Shown once, never stored. Only its hash is kept, exactly as a password reset token is.
/// </param>
public sealed record SubAgentInvited(Guid AgencyId, string Email, string InvitationToken, DateTimeOffset ExpiresAt);

/// <summary>
/// A principal's own network: creating sub-agents beneath it, and freezing or ending them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two levels and no more</b> (build-plan decision 7). <see cref="Agency.RegisterSubAgent"/>
/// refuses a sub-agent of a sub-agent, and a CHECK constraint on <c>agencies</c> refuses it again;
/// this service refuses it a third time, with a sentence an agent can act on, before either fires.
/// </para>
/// <para>
/// <b>No cross-tenant read.</b> A principal sees its own sub-agents because <c>Agency</c>'s query
/// filter already says so — "me, or anything whose parent is me". Nothing here opens a platform
/// scope, and nothing here calls <c>IgnoreQueryFilters</c>.
/// </para>
/// <para>
/// <b>Freeze and revoke are the agency's own lifecycle, reused.</b> A frozen sub-agent is
/// <see cref="AgencyStatus.Suspended"/>: its staff can still sign in and read, and it cannot sell
/// — which is exactly what <see cref="AgencyAccess"/> already says suspension means. A revoked one
/// is <see cref="AgencyStatus.Terminated"/>. Reusing the states means the storefront, the checkout
/// and the sign-in path need no new rule, and cannot disagree about one.
/// </para>
/// </remarks>
public sealed class SubAgentNetworkService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ITransactionRunner _transactions;
    private readonly IPlatformScope _platformScope;
    private readonly ISubAgentEntitlement _entitlement;
    private readonly ITokenHasher _tokens;
    private readonly IEmailSender _email;
    private readonly SubAgentInviteLinkBuilder _inviteLinks;
    private readonly TimeProvider _clock;

    public SubAgentNetworkService(
        IAppDbContext db,
        ITenantContext tenant,
        ITransactionRunner transactions,
        IPlatformScope platformScope,
        ISubAgentEntitlement entitlement,
        ITokenHasher tokens,
        IEmailSender email,
        SubAgentInviteLinkBuilder inviteLinks,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _transactions = transactions;
        _platformScope = platformScope;
        _entitlement = entitlement;
        _tokens = tokens;
        _email = email;
        _inviteLinks = inviteLinks;
        _clock = clock;
    }

    // ------------------------------------------------------------------------ reading

    /// <summary>The principal's sub-agents, with what each may sell and spend.</summary>
    public async Task<SubAgentNetwork> ListAsync(CancellationToken cancellationToken = default)
    {
        var principalId = await RequirePrincipalAsync(cancellationToken);

        // "Parent is me" — the Agency filter already limits this to the caller's own network, and
        // the predicate is repeated so the query reads correctly on its own.
        var agencies = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.ParentAgencyId == principalId)
            .OrderBy(agency => agency.LegalName)
            .ToListAsync(cancellationToken);

        var ids = agencies.Select(agency => agency.Id).ToList();
        var now = _clock.GetUtcNow();

        var openInvitations = await _db.UserInvitations
            .AsNoTracking()
            .Where(invitation => invitation.AgencyId != null
                                 && ids.Contains(invitation.AgencyId.Value)
                                 && invitation.AcceptedAt == null
                                 && invitation.RevokedAt == null
                                 && invitation.ExpiresAt > now)
            .Select(invitation => invitation.AgencyId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var scopes = await _db.SubAgentScopes
            .AsNoTracking()
            .Where(scope => scope.AgencyId == principalId)
            .GroupBy(scope => scope.SubAgencyId)
            .Select(group => new { SubAgencyId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var overrides = await _db.PermissionOverrides
            .AsNoTracking()
            .Where(entry => entry.AgencyId == principalId)
            .Select(entry => new { entry.SubAgencyId, entry.PermissionCode })
            .ToListAsync(cancellationToken);

        var allowances = await _db.WalletAllowances
            .AsNoTracking()
            .Where(allowance => allowance.AgencyId == principalId)
            .Select(allowance => new
            {
                allowance.SubAgencyId,
                allowance.SpentMinor,
                allowance.LimitMinor,
                allowance.Currency,
            })
            .ToListAsync(cancellationToken);

        var summaries = agencies.Select(agency =>
        {
            var allowance = allowances.Find(candidate => candidate.SubAgencyId == agency.Id);
            var denied = overrides.Where(entry => entry.SubAgencyId == agency.Id).ToList();

            return new SubAgentSummary(
                agency.Id,
                agency.LegalName,
                agency.TradingName,
                agency.Slug,
                agency.Status,
                agency.StatusReason,
                agency.CreatedAt,
                openInvitations.Contains(agency.Id),
                scopes.Find(candidate => candidate.SubAgencyId == agency.Id)?.Count ?? 0,
                denied.Count,
                !denied.Exists(entry => entry.PermissionCode == PermissionCodes.MarginView),
                allowance?.SpentMinor.AmountMinor,
                allowance?.LimitMinor.AmountMinor,
                allowance?.Currency);
        }).ToList();

        var entitlement = await _entitlement.MayAddSubAgentAsync(principalId, agencies.Count, cancellationToken);

        return new SubAgentNetwork(
            summaries,
            entitlement.Limit,
            entitlement.IsAllowed,
            entitlement.Refusal);
    }

    // ------------------------------------------------------------------------ inviting

    /// <summary>
    /// Creates a sub-agency beneath the caller and invites its first user, in one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Either both exist or neither does. A sub-agency with nobody able to sign in is a support
    /// ticket; an invitation to an agency that was never created is a broken link.
    /// </para>
    /// <para>
    /// The invitation token is returned once and stored only as a keyed hash, so a database dump
    /// cannot be used to accept somebody else's invitation. The email carries the same token and
    /// the <i>principal's</i> branding — see <see cref="SubAgentInvitationEmail"/>.
    /// </para>
    /// </remarks>
    public async Task<SubAgentInvited> InviteAsync(
        string legalName,
        string? tradingName,
        string email,
        CancellationToken cancellationToken = default)
    {
        var principalId = await RequirePrincipalAsync(cancellationToken);
        var address = (email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid, "A sub-agent needs a business name.");
        }

        if (address.Length < 3 || !address.Contains('@', StringComparison.Ordinal))
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid, "Enter a valid email address for the person who will run it.");
        }

        var existing = await _db.Agencies
            .AsNoTracking()
            .CountAsync(agency => agency.ParentAgencyId == principalId, cancellationToken);

        // The one entitlement question this feature asks. See ISubAgentEntitlement.
        var entitlement = await _entitlement.MayAddSubAgentAsync(principalId, existing, cancellationToken);

        if (!entitlement.IsAllowed)
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.EntitlementExhausted,
                "Your plan does not allow another sub-agent.",
                entitlement.Refusal);
        }

        var principal = await _db.Agencies
            .AsNoTracking()
            .SingleAsync(agency => agency.Id == principalId, cancellationToken);

        var role = await _db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.AgencyId == null && candidate.Name == Role.SystemRoles.Owner,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The system Owner role does not exist, so a new sub-agent has nobody to own it. "
                + "Reference data has not been loaded — run the migrate command.");

        var invitedBy = _tenant.UserId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "Only a signed-in user can invite a sub-agent.");

        var token = _tokens.GenerateOpaqueToken();
        var now = _clock.GetUtcNow();

        var (agencyId, expiresAt) = await _transactions.RunAsync(
            async ct =>
            {
                var subAgency = Agency.RegisterSubAgent(
                    principal,
                    legalName,
                    await UniqueSlugAsync(legalName, ct),
                    tradingName);

                var invitation = UserInvitation.Create(
                    subAgency.Id, address, role.Id, _tokens.Hash(token), invitedBy, now);

                _db.Agencies.Add(subAgency);
                _db.AgencySettings.Add(AgencySettings.CreateDefault(subAgency));
                _db.AgencyBranding.Add(AgencyBranding.CreateDefault(subAgency));
                _db.UserInvitations.Add(invitation);

                await _db.SaveChangesAsync(ct);

                return (subAgency.Id, invitation.ExpiresAt);
            },
            cancellationToken);

        await SendInvitationAsync(principalId, principal, address, legalName, token, cancellationToken);

        return new SubAgentInvited(agencyId, address, token, expiresAt);
    }

    // ------------------------------------------------------------------------ freeze and revoke

    /// <summary>
    /// Stops a sub-agent selling, without locking its staff out.
    /// </summary>
    /// <remarks>
    /// Its allowance is frozen at the same time, so a booking already in flight cannot draw on the
    /// principal's money while the freeze is being decided.
    /// </remarks>
    public Task FreezeAsync(Guid subAgencyId, string reason, CancellationToken cancellationToken = default) =>
        ChangeStandingAsync(subAgencyId, reason, freeze: true, revoke: false, cancellationToken);

    /// <summary>Lifts a freeze, and lets the allowance be drawn on again.</summary>
    public Task UnfreezeAsync(Guid subAgencyId, string reason, CancellationToken cancellationToken = default) =>
        ChangeStandingAsync(subAgencyId, reason, freeze: false, revoke: false, cancellationToken);

    /// <summary>
    /// Ends the relationship: the sub-agency is terminated, its sessions stop working on their
    /// next refresh, its open invitations are cancelled and its allowance goes to zero.
    /// </summary>
    /// <remarks>
    /// Bookings it already made stay exactly where they are, readable by the principal, because
    /// the agency row is never deleted — build-plan decision 14 and open question 14.
    /// </remarks>
    public Task RevokeAsync(Guid subAgencyId, string reason, CancellationToken cancellationToken = default) =>
        ChangeStandingAsync(subAgencyId, reason, freeze: false, revoke: true, cancellationToken);

    private async Task ChangeStandingAsync(
        Guid subAgencyId,
        string reason,
        bool freeze,
        bool revoke,
        CancellationToken cancellationToken)
    {
        var principalId = await RequirePrincipalAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid,
                "Say why.",
                "Freezing or ending a sub-agent is written to the audit log, and the reason is what makes that record useful.");
        }

        await _transactions.RunAsync(
            async ct =>
            {
                var subAgency = await _db.Agencies
                    .SingleOrDefaultAsync(
                        agency => agency.Id == subAgencyId && agency.ParentAgencyId == principalId, ct)
                    ?? throw new SubAgentRefusedException(
                        SubAgentRefusal.NotFound, "That sub-agent is not one of yours.");

                if (subAgency.Status == AgencyStatus.Terminated)
                {
                    throw new SubAgentRefusedException(
                        SubAgentRefusal.Conflict,
                        "That sub-agent has already been ended.",
                        "A terminated agency is kept for its records and is never brought back.");
                }

                var now = _clock.GetUtcNow();

                if (revoke)
                {
                    subAgency.Terminate(reason, now);

                    // Every session fails on its next refresh, and any invitation nobody has
                    // accepted stops working now rather than in a week's time.
                    await RevokeAccessAsync(subAgencyId, now, ct);
                }
                else if (freeze)
                {
                    subAgency.Suspend(reason, now);
                }
                else
                {
                    subAgency.Reinstate(reason, now);
                }

                var allowance = await _db.WalletAllowances
                    .SingleOrDefaultAsync(candidate => candidate.SubAgencyId == subAgencyId, ct);

                if (allowance is not null)
                {
                    if (revoke)
                    {
                        allowance.ChangeLimit(Money.Zero);
                        allowance.Freeze();
                    }
                    else if (freeze)
                    {
                        allowance.Freeze();
                    }
                    else
                    {
                        allowance.Unfreeze();
                    }
                }

                await _db.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);
    }

    private async Task RevokeAccessAsync(Guid subAgencyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var invitations = await _db.UserInvitations
            .Where(invitation => invitation.AgencyId == subAgencyId
                                 && invitation.AcceptedAt == null
                                 && invitation.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var invitation in invitations)
        {
            invitation.Revoke(now);
        }

        var userIds = await _db.Users
            .Where(user => user.AgencyId == subAgencyId)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var tokens = await _db.RefreshTokens
            .Where(token => userIds.Contains(token.UserId) && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke(now);
        }
    }

    // ------------------------------------------------------------------------ helpers

    private async Task SendInvitationAsync(
        Guid principalId,
        Agency principal,
        string address,
        string legalName,
        string token,
        CancellationToken cancellationToken)
    {
        var branding = await _db.AgencyBranding
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AgencyId == principalId, cancellationToken);

        var message = SubAgentInvitationEmail.Create(
            address, legalName, principal, branding, _inviteLinks.Build(token));

        await _email.SendAsync(message, cancellationToken);
    }

    /// <summary>The caller's agency, if it is a principal. Throws with a usable sentence if not.</summary>
    private async Task<Guid> RequirePrincipalAsync(CancellationToken cancellationToken)
    {
        var agencyId = _tenant.AgencyId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "This request has no agency, so it has no network.");

        var type = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Type)
            .SingleOrDefaultAsync(cancellationToken);

        if (type != AgencyType.Principal)
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden,
                "Only a principal can manage sub-agents.",
                "The network is two levels deep — a principal and the agents beneath it — so a "
                + "sub-agent has none of its own.");
        }

        return agencyId;
    }

    /// <summary>
    /// A slug nobody is using. The unique index is still the real guarantee; this only keeps the
    /// common case from failing.
    /// </summary>
    private async Task<string> UniqueSlugAsync(string legalName, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(legalName);
        var candidate = baseSlug;

        // A slug is unique platform-wide — it is in storefront URLs — so this one check has to
        // see agencies outside the caller's network. The audited scope, never IgnoreQueryFilters:
        // it reads nothing but the slug column and says in the log why it had to.
        using var scope = _platformScope.Enter(
            "sub-agent creation — an agency slug is unique across every agency, not just this network");

        for (var suffix = 2;
             await _db.Agencies.AnyAsync(a => a.Slug == candidate, cancellationToken);
             suffix++)
        {
            candidate = $"{baseSlug}-{suffix}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        var builder = new System.Text.StringBuilder();
        var previousWasHyphen = true;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        var slug = builder.ToString().TrimEnd('-');

        if (slug.Length > 50)
        {
            slug = slug[..50].TrimEnd('-');
        }

        return slug.Length == 0 ? "sub-agent" : slug;
    }
}
