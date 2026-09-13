using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Platform;

/// <summary>What happened to an admin action against one agency.</summary>
public abstract record AgencyActionOutcome
{
    private AgencyActionOutcome()
    {
    }

    /// <summary>It was done, and here is the agency's standing afterwards.</summary>
    public sealed record Done(AgencyStatusResponse Status) : AgencyActionOutcome;

    /// <summary>There is no agency with that id.</summary>
    public sealed record NotFound : AgencyActionOutcome;

    /// <summary>The reason was missing or blank. Every action here demands one.</summary>
    public sealed record ReasonRequired : AgencyActionOutcome;

    /// <summary>The agency is not in a state this action makes sense from.</summary>
    /// <param name="Detail">What it is, and what it would have to be.</param>
    public sealed record NotAllowed(string Detail) : AgencyActionOutcome;
}

/// <summary>
/// The admin actions that change an agency's standing: verify, edit, suspend, reinstate, terminate.
/// </summary>
/// <remarks>
/// <para>
/// Three rules hold for all of them, and they are why this is one class rather than five handlers.
/// </para>
/// <list type="number">
///   <item>
///     <b>A reason is mandatory.</b> Refused before anything is loaded, so a blank reason cannot
///     reach the database. It is written to <c>platform.audit_logs</c> and kept on the agency row.
///   </item>
///   <item>
///     <b>The audit entry is automatic.</b> <c>Agency</c> implements <c>IAuditLogged</c>, so the
///     save interceptor records actor, time and before/after state; all this class does is set
///     the reason immediately before saving.
///   </item>
///   <item>
///     <b>The read crosses agencies</b>, so it runs inside <see cref="IPlatformScope"/> with a
///     reason of its own, which is logged. Never <c>IgnoreQueryFilters</c>.
///   </item>
/// </list>
/// <para>
/// What suspension <i>means</i> is not here — it is in <see cref="AgencyAccess"/>, so the
/// checkout and the storefront read the same rule rather than each deciding for itself.
/// </para>
/// </remarks>
public sealed partial class AgencyLifecycleService
{
    /// <summary>The audit action names these write. Lower_snake_case, prefixed with the subject.</summary>
    public static class Actions
    {
        public const string Updated = "agency.profile_updated";
        public const string Verified = "agency.verified";
        public const string Suspended = "agency.suspended";
        public const string Reinstated = "agency.reinstated";
        public const string Terminated = "agency.terminated";
        public const string Exported = "agency.exported";
    }

    /// <summary>Long enough to explain, short enough to fit the audit log's reason column.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Short enough that "yes" is not an explanation anyone can defend later.</summary>
    public const int MinReasonLength = 10;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgencyLifecycleService> _logger;

    public AgencyLifecycleService(
        IAppDbContext db,
        IPlatformScope platformScope,
        IAuditContext audit,
        TimeProvider clock,
        ILogger<AgencyLifecycleService> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Corrects an agency's business details, with a reason and an audit entry.</summary>
    public async Task<AgencyActionOutcome> UpdateAsync(
        Guid agencyId,
        UpdateAgencyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await ActAsync(
            agencyId,
            request.Reason,
            "Admin console — edits one agency's business details",
            Actions.Updated,
            agency =>
            {
                agency.UpdateProfile(
                    request.LegalName,
                    request.TradingName,
                    request.TaxId,
                    request.Timezone,
                    request.VatRateBasisPoints);

                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Verifies an agency by hand.
    /// </summary>
    /// <remarks>
    /// The ordinary route to verified is the KYB queue, which also opens the wallet. This exists
    /// for the agency whose paperwork arrived by another road entirely — and because an admin who
    /// cannot do it in the console does it in psql, where nothing records that they did.
    /// </remarks>
    public Task<AgencyActionOutcome> VerifyAsync(
        Guid agencyId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ActAsync(
            agencyId,
            reason,
            "Admin console — verifies one agency by hand",
            Actions.Verified,
            agency => agency.Status switch
            {
                AgencyStatus.Verified => "This agency is already verified.",
                AgencyStatus.Terminated => "A terminated agency cannot be verified.",
                _ => Do(() => agency.MarkVerified(_clock.GetUtcNow())),
            },
            cancellationToken);

    /// <summary>
    /// Suspends an agency: no new bookings, and the storefront goes offline.
    /// </summary>
    /// <remarks>
    /// Build-plan decision 14. Nothing already sold is touched — the orders stand, the travellers
    /// keep the documents their magic link serves, and support goes on servicing them. That is
    /// deliberate, and it is the reason this method changes one column instead of cancelling
    /// anything.
    /// </remarks>
    public Task<AgencyActionOutcome> SuspendAsync(
        Guid agencyId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ActAsync(
            agencyId,
            reason,
            "Admin console — suspends one agency",
            Actions.Suspended,
            agency => agency.Status switch
            {
                AgencyStatus.Suspended => "This agency is already suspended.",
                AgencyStatus.Terminated => "A terminated agency cannot be suspended.",
                _ => Do(() => agency.Suspend(reason, _clock.GetUtcNow())),
            },
            cancellationToken);

    /// <summary>Lifts a suspension.</summary>
    public Task<AgencyActionOutcome> ReinstateAsync(
        Guid agencyId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ActAsync(
            agencyId,
            reason,
            "Admin console — lifts one agency's suspension",
            Actions.Reinstated,
            agency => agency.Status is AgencyStatus.Suspended
                ? Do(() => agency.Reinstate(reason, _clock.GetUtcNow()))
                : $"Only a suspended agency can be reinstated, and this one is {agency.Status}.",
            cancellationToken);

    /// <summary>
    /// Ends the relationship for good.
    /// </summary>
    /// <remarks>
    /// The row is never deleted: orders, invoices and ledger entries point at it, and a tax
    /// authority may ask about them years from now. Take the export first — the console's flow
    /// insists on it, and <see cref="AgencyExportService"/> is what produces it.
    /// </remarks>
    public Task<AgencyActionOutcome> TerminateAsync(
        Guid agencyId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ActAsync(
            agencyId,
            reason,
            "Admin console — terminates one agency",
            Actions.Terminated,
            agency => agency.Status is AgencyStatus.Terminated
                ? "This agency is already terminated."
                : Do(() => agency.Terminate(reason, _clock.GetUtcNow())),
            cancellationToken);

    /// <summary>Is this reason good enough to stand in an audit log?</summary>
    public static bool IsUsableReason(string? reason) =>
        reason is not null
        && reason.Trim().Length >= MinReasonLength
        && reason.Trim().Length <= MaxReasonLength;

    /// <summary>Runs one action's shape: check the reason, load, apply, audit, save.</summary>
    /// <param name="apply">
    /// Applies the change, or returns why it cannot be applied. Returning null means it was.
    /// </param>
    private async Task<AgencyActionOutcome> ActAsync(
        Guid agencyId,
        string reason,
        string scopeReason,
        string auditAction,
        Func<Agency, string?> apply,
        CancellationToken cancellationToken)
    {
        if (!IsUsableReason(reason))
        {
            return new AgencyActionOutcome.ReasonRequired();
        }

        var trimmed = reason.Trim();

        using var scope = _platformScope.Enter(scopeReason);

        var agency = await _db.Agencies.FirstOrDefaultAsync(candidate => candidate.Id == agencyId, cancellationToken);

        if (agency is null)
        {
            return new AgencyActionOutcome.NotFound();
        }

        if (apply(agency) is { } refusal)
        {
            return new AgencyActionOutcome.NotAllowed(refusal);
        }

        // Set as late as possible: the reason travels with everything in this SaveChanges, so one
        // left over from earlier in the request would be attached to a change it does not describe.
        _audit.SetReason($"{auditAction}: {trimmed}");
        await _db.SaveChangesAsync(cancellationToken);

        LogActed(_logger, auditAction, agencyId, _audit.ActorUserId);

        return new AgencyActionOutcome.Done(new AgencyStatusResponse(
            agency.Id,
            agency.Status.ToString(),
            agency.StatusChangedAt ?? _clock.GetUtcNow(),
            trimmed,
            AgencyAccess.CanTakeNewBookings(agency.Status),
            AgencyAccess.CanServeStorefront(agency.Status)));
    }

    /// <summary>Runs the change and reports "no refusal", so each switch arm stays one expression.</summary>
    private static string? Do(Action change)
    {
        change();
        return null;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{Action} on agency {AgencyId} by {ActorUserId}.")]
    private static partial void LogActed(ILogger logger, string action, Guid agencyId, Guid? actorUserId);
}
