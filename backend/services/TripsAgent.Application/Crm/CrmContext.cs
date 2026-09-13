using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Application.Crm;

/// <summary>The agency a CRM request is for, and "now" and "today" where it is.</summary>
/// <param name="Currency">The agency's own currency: leads and quotes are in it (open question 17).</param>
/// <param name="TimeZone">The agency's IANA time zone: <c>Africa/Lagos</c>.</param>
/// <param name="Today">
/// Today in the agency's time zone. A quote valid until the 3rd can be accepted until midnight in
/// Lagos, not until midnight UTC an hour earlier.
/// </param>
public sealed record CrmAgency(Guid Id, string Currency, string TimeZone, DateTimeOffset Now, DateOnly Today);

/// <summary>The agency and the person a CRM request acts for.</summary>
/// <param name="UserId">The person at the agency. Null when nobody signed in is acting.</param>
/// <param name="Name">Their name, as the history and the timeline show it.</param>
public sealed record CrmActor(CrmAgency Agency, Guid? UserId, string Name)
{
    public Guid AgencyId => Agency.Id;

    public DateTimeOffset Now => Agency.Now;

    public DateOnly Today => Agency.Today;
}

/// <summary>Reads who and when a CRM request is, once, for every service that needs it.</summary>
public sealed class CrmContext
{
    /// <summary>Who opened a lead that came from the storefront, in its history.</summary>
    public const string WebsiteName = "Website";

    /// <summary>The name shown for someone at the agency whose account cannot be read.</summary>
    public const string TeamName = "Agency team";

    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public CrmContext(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>The agency this request acts for.</summary>
    /// <exception cref="InvalidOperationException">No agency is resolved: a CRM service was called outside any tenant.</exception>
    public Guid AgencyId => _tenant.AgencyId ?? throw new InvalidOperationException(
        "The CRM belongs to an agency, and none is resolved for this request.");

    /// <summary>The agency, with its currency, its time zone, and the time there.</summary>
    public async Task<CrmAgency> AgencyAsync(CancellationToken cancellationToken = default)
    {
        var agencyId = AgencyId;

        var agency = await _db.Agencies.AsNoTracking()
            .Where(candidate => candidate.Id == agencyId)
            .Select(candidate => new { candidate.BaseCurrency, candidate.Timezone })
            .FirstAsync(cancellationToken);

        var now = Now;

        return new CrmAgency(agencyId, agency.BaseCurrency, agency.Timezone, now, Today(now, agency.Timezone));
    }

    /// <summary>The agency, and the signed-in person acting for it.</summary>
    public async Task<CrmActor> ActorAsync(CancellationToken cancellationToken = default)
    {
        var agency = await AgencyAsync(cancellationToken);

        if (_tenant.UserId is not { } userId)
        {
            return new CrmActor(agency, null, WebsiteName);
        }

        var user = await _db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.FirstName, candidate.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        return new CrmActor(agency, userId, PersonName(user?.FirstName, user?.LastName) ?? TeamName);
    }

    /// <summary>"Ada Obi", or null when there is no name to show.</summary>
    public static string? PersonName(string? firstName, string? lastName)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>The date at <paramref name="now"/> in <paramref name="timeZone"/>. An unknown zone reads as UTC.</summary>
    public static DateOnly Today(DateTimeOffset now, string timeZone) =>
        DateOnly.FromDateTime(InZone(now, timeZone).DateTime);

    /// <summary><paramref name="instant"/> as the clock on the wall in <paramref name="timeZone"/> shows it.</summary>
    public static DateTimeOffset InZone(DateTimeOffset instant, string timeZone) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone) ? TimeZoneInfo.ConvertTime(instant, zone) : instant;
}
