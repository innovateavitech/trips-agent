using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.Application.Catalog;

/// <summary>What one run of the waitlist sweep did.</summary>
/// <param name="Expired">Offers whose deadline passed.</param>
/// <param name="Offered">Seats offered to the next person waiting.</param>
public sealed record WaitlistSweepRun(int Expired, int Offered);

/// <summary>
/// The queue for a departure whose seats have gone: who is waiting, who has been offered a seat,
/// and what happens when they do not answer (FRD §2.13 RS-6, plan §3 job 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>An offer does not hold a seat.</b> It is a promise of first refusal for a fixed time, and the
/// seats it promises are counted against what is free before anybody else is offered anything — so
/// three people are never offered the same seat, and nothing is reserved against a person who may
/// never reply. The seat is only taken when they buy it, through <see cref="DepartureSeats"/>.
/// </para>
/// <para>
/// <b>Offers roll on.</b> When an offer's deadline passes it expires and the seat is offered to the
/// next person waiting, in the order they joined. That is the whole of job 10: the sweep expires,
/// and expiring frees a seat, and a freed seat is offered.
/// </para>
/// <para>
/// Offers go by email only — the build plan's own note on what the MVP leaves out.
/// </para>
/// </remarks>
public sealed partial class DepartureWaitlistService
{
    /// <summary>How long somebody has to take a seat they have been offered.</summary>
    public static readonly TimeSpan DefaultOfferTimeToLive = TimeSpan.FromHours(48);

    /// <summary>A ceiling on entries handled per run, so one run cannot hold a Worker for ever.</summary>
    public const int MaxPerRun = 200;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<DepartureWaitlistService> _logger;

    public DepartureWaitlistService(
        IAppDbContext db,
        IPlatformScope platformScope,
        INotifier notifier,
        TimeProvider clock,
        ILogger<DepartureWaitlistService> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Offers whatever seats are free on <paramref name="departureId"/> to the people waiting for
    /// them, earliest first. Called whenever capacity frees up (job 9) and by the sweep below.
    /// </summary>
    /// <returns>How many entries were offered a seat.</returns>
    public async Task<int> OfferFreedSeatsAsync(
        Guid departureId,
        TimeSpan? offerTimeToLive = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var departure = await _db.Departures.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == departureId, cancellationToken);

        // Nothing is offered on a departure that cannot be sold: closed, cancelled, or past its
        // cutoff. An offer nobody could act on is worse than no offer.
        if (departure is null
            || departure.Status is DepartureStatus.Closed or DepartureStatus.Cancelled
            || now >= departure.CutoffAt)
        {
            return 0;
        }

        var queue = await _db.DepartureWaitlist
            .Where(entry => entry.DepartureId == departureId)
            .Where(entry => entry.Status == WaitlistStatus.Waiting || entry.Status == WaitlistStatus.Offered)
            .OrderBy(entry => entry.JoinedAt)
            .ToListAsync(cancellationToken);

        // Seats already promised to somebody with an open offer are not free to promise again.
        var promised = queue.Where(entry => entry.Status == WaitlistStatus.Offered).Sum(entry => entry.PaxCount);
        var free = departure.Seats.SeatsLeft - promised;

        if (free <= 0)
        {
            return 0;
        }

        var title = await ProductTitleAsync(departure.ProductId, cancellationToken);
        var offered = 0;

        foreach (var entry in queue.Where(entry => entry.Status == WaitlistStatus.Waiting))
        {
            // A party larger than what is left is passed over rather than offered a seat it cannot
            // use — and stays in the queue, in its place, for when more comes back.
            if (entry.PaxCount > free)
            {
                continue;
            }

            entry.Offer(now, offerTimeToLive ?? DefaultOfferTimeToLive);
            free -= entry.PaxCount;
            offered++;

            await SendOfferAsync(departure, entry, title, cancellationToken);

            if (free <= 0)
            {
                break;
            }
        }

        if (offered > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            LogOffered(_logger, departureId, offered);
        }

        return offered;
    }

    /// <summary>
    /// Job 10: expires every offer whose deadline has passed, then offers the seats they were
    /// holding open to the next people waiting. Runs across every agency.
    /// </summary>
    public async Task<WaitlistSweepRun> SweepAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "waitlist offer expiry — rolls every agency's unanswered seat offers on to the next person");

        var now = _clock.GetUtcNow();

        var lapsed = await _db.DepartureWaitlist
            .Where(entry => entry.Status == WaitlistStatus.Offered && entry.ExpiresAt != null && entry.ExpiresAt <= now)
            .OrderBy(entry => entry.ExpiresAt)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        var expired = 0;

        foreach (var entry in lapsed)
        {
            if (entry.Expire())
            {
                expired++;
            }
        }

        if (expired > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            LogExpired(_logger, expired);
        }

        // Every departure that just had an offer lapse, and every one whose seats came back some
        // other way, gets its queue walked again.
        var departureIds = lapsed.Select(entry => entry.DepartureId).Distinct().ToList();
        var offered = 0;

        foreach (var departureId in departureIds)
        {
            offered += await OfferFreedSeatsAsync(departureId, cancellationToken: cancellationToken);
        }

        return new WaitlistSweepRun(expired, offered);
    }

    private async Task SendOfferAsync(
        Departure departure,
        DepartureWaitlistEntry entry,
        string title,
        CancellationToken cancellationToken)
    {
        var zone = await AgencyZoneAsync(departure.AgencyId, cancellationToken);

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                departure.AgencyId,
                NotificationTemplateCatalog.DepartureWaitlistOffer,
                entry.Email,
                entry.Name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["departureTitle"] = title,
                    ["departureDate"] = Day(departure.DepartureDate),
                    ["paxCount"] = entry.PaxCount.ToString(CultureInfo.InvariantCulture),
                    ["offerExpiresAt"] = Instant(entry.ExpiresAt!.Value, zone),
                },
                $"{NotificationTemplateCatalog.DepartureWaitlistOffer}:{entry.Id}"),
            cancellationToken);
    }

    private async Task<string> ProductTitleAsync(Guid productId, CancellationToken cancellationToken) =>
        await _db.Products.AsNoTracking()
            .Where(product => product.Id == productId)
            .Select(product => product.Title)
            .FirstOrDefaultAsync(cancellationToken)
        ?? "your departure";

    /// <summary>
    /// The agency's own time zone, so a deadline reads as the hour the traveller keeps rather than
    /// the hour a server keeps. UTC when the agency names a zone this machine does not have.
    /// </summary>
    private async Task<TimeZoneInfo> AgencyZoneAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var id = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == agencyId)
            .Select(agency => agency.Timezone)
            .FirstOrDefaultAsync(cancellationToken);

        return id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : TimeZoneInfo.Utc;
    }

    private static string Day(DateOnly date) =>
        date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information, Message = "Offered {Offered} waitlist entries a seat on departure {DepartureId}.")]
    private static partial void LogOffered(ILogger logger, Guid departureId, int offered);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "Expired {Expired} unanswered waitlist offers.")]
    private static partial void LogExpired(ILogger logger, int expired);
}
