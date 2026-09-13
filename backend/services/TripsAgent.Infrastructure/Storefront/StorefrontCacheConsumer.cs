using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>
/// Plan §3, job 15: keeps what travellers see in step with what agencies publish.
/// </summary>
/// <remarks>
/// <para>
/// Two things make a storefront stale. A site is published or rolled back, and its pages now say the
/// wrong thing. Or its hostnames change — one added, proved, removed or promoted to primary — and the
/// routing cache now points the wrong way, including for the hostnames that did <i>not</i> change,
/// whose canonical address just moved.
/// </para>
/// <para>
/// Both end here, and both do the same two things: drop the routing cache, and ask the storefront to
/// rebuild that site's pages. Neither throws. The change is already committed, and a cache that
/// refreshes late is a nuisance; a message that fails and redelivers forever is an outage.
/// </para>
/// </remarks>
public sealed partial class StorefrontCacheConsumer :
    IConsumer<SitePublished>,
    IConsumer<SiteDomainsChanged>
{
    private readonly IStorefrontHostCache _hostCache;
    private readonly IStorefrontRevalidator _revalidator;
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ILogger<StorefrontCacheConsumer> _logger;

    public StorefrontCacheConsumer(
        IStorefrontHostCache hostCache,
        IStorefrontRevalidator revalidator,
        IAppDbContext db,
        IPlatformScope platformScope,
        ILogger<StorefrontCacheConsumer> logger)
    {
        _hostCache = hostCache;
        _revalidator = revalidator;
        _db = db;
        _platformScope = platformScope;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SitePublished> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        LogPublished(_logger, message.SiteId, message.VersionNumber, message.IsRollback);

        await RefreshAsync(message.SiteId, known: [], context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<SiteDomainsChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        await RefreshAsync(message.SiteId, message.Hostnames, context.CancellationToken);
    }

    private async Task RefreshAsync(Guid siteId, IReadOnlyList<string> known, CancellationToken cancellationToken)
    {
        // Wholesale: hostnames are few and change rarely, and one change moves the canonical address
        // cached against every other hostname of the same site.
        await _hostCache.InvalidateAllAsync(cancellationToken);

        var hostnames = await HostnamesAsync(siteId, cancellationToken);

        // A hostname that was just removed is no longer on the site, so the event carries it: the
        // storefront still holds pages under it and has to be told to drop them.
        var all = hostnames
            .Concat(known)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hostname => hostname, StringComparer.Ordinal)
            .ToList();

        await _revalidator.RevalidateAsync(siteId, all, cancellationToken);
    }

    /// <summary>
    /// The site's hostnames. Read across agencies because this runs on a background message with no
    /// caller and therefore no tenant of its own.
    /// </summary>
    private async Task<List<string>> HostnamesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        using (_platformScope.Enter(
                   "Storefront cache invalidation — lists the hostnames of the site whose pages changed, "
                   + "so the storefront can be told which cached pages to rebuild."))
        {
            return await _db.SiteDomains.AsNoTracking()
                .Where(domain => domain.SiteId == siteId)
                .Select(domain => domain.Hostname)
                .ToListAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Site {SiteId} is now serving version {VersionNumber} (rollback: {IsRollback}); its cached pages "
                  + "are being rebuilt.")]
    private static partial void LogPublished(ILogger logger, Guid siteId, int versionNumber, bool isRollback);
}
