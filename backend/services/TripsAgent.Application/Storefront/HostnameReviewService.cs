using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// The platform side of open question 20: a website address that looks like a well-known brand waits here
/// until someone clears it. A simple flag, as decided for the MVP.
/// </summary>
/// <remarks>
/// Platform staff only, and every read crosses agencies — through <see cref="IPlatformScope"/>, which logs why.
/// A flagged address serves nothing, so leaving one here is always safe; clearing it is the only action.
/// </remarks>
public sealed class HostnameReviewService
{
    /// <summary>The longest the queue gets in one read.</summary>
    public const int QueueLimit = 200;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ITenantContext _tenant;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _clock;

    public HostnameReviewService(IAppDbContext db, IPlatformScope platformScope, ITenantContext tenant, IOutbox outbox, TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _tenant = tenant;
        _outbox = outbox;
        _clock = clock;
    }

    /// <summary>Every address waiting for a review, oldest first.</summary>
    public async Task<IReadOnlyList<HostnameReviewResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        using (_platformScope.Enter("hostname review queue — lists website addresses set aside for review, from every agency"))
        {
            var rows = await (
                    from domain in _db.SiteDomains.AsNoTracking()
                    join agency in _db.Agencies.AsNoTracking() on domain.AgencyId equals agency.Id
                    where domain.NeedsReview
                    orderby domain.CreatedAt
                    select new { Domain = domain, agency.LegalName, agency.TradingName })
                .Take(QueueLimit)
                .ToListAsync(cancellationToken);

            return rows.Select(row => ToResponse(row.Domain, row.TradingName ?? row.LegalName)).ToList();
        }
    }

    /// <summary>
    /// Clears an address: it serves its site from now on, and the alert that asked for the review is resolved.
    /// </summary>
    public async Task<StorefrontResult<HostnameReviewResponse>> ApproveAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        using (_platformScope.Enter("hostname review — clears one website address that was set aside for review"))
        {
            var domain = await _db.SiteDomains.FirstOrDefaultAsync(candidate => candidate.Id == domainId, cancellationToken);

            if (domain is null)
            {
                return new StorefrontResult<HostnameReviewResponse>.NotFound("There is no website address with that id.");
            }

            if (!domain.NeedsReview)
            {
                return new StorefrontResult<HostnameReviewResponse>.Conflict(
                    "That address is not waiting for a review.",
                    "Someone may have cleared it already.");
            }

            var now = _clock.GetUtcNow();
            domain.ClearReview(_tenant.UserId, now);

            var alerts = await _db.AdminAlerts
                .Where(alert => alert.Type == AdminAlertType.HostnameReview
                                && alert.EntityId == domain.Id
                                && alert.Status != AdminAlertStatus.Resolved)
                .ToListAsync(cancellationToken);

            foreach (var alert in alerts)
            {
                alert.Resolve(now);
            }

            if (domain.IsVerified)
            {
                // It serves from now on, so whatever remembered it as unknown must forget.
                _outbox.Enqueue(new SiteDomainsChanged(domain.SiteId, domain.AgencyId, [domain.Hostname]), domain.AgencyId);
            }

            await _db.SaveChangesAsync(cancellationToken);

            var agency = await _db.Agencies.AsNoTracking()
                .Where(candidate => candidate.Id == domain.AgencyId)
                .Select(candidate => candidate.TradingName ?? candidate.LegalName)
                .FirstAsync(cancellationToken);

            return new StorefrontResult<HostnameReviewResponse>.Ok(ToResponse(domain, agency));
        }
    }

    private static HostnameReviewResponse ToResponse(SiteDomain domain, string agencyName) => new(
        domain.Id,
        domain.Hostname,
        domain.Type.ToString(),
        domain.VerificationStatus.ToString(),
        domain.NeedsReview,
        domain.AgencyId,
        agencyName,
        domain.CreatedAt);
}
