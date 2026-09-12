using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Platform;

/// <summary>How the directory is sorted.</summary>
public enum AgencySort
{
    /// <summary>Newest signup first. The default: the interesting account is usually the new one.</summary>
    Newest = 0,
    Oldest = 1,
    Name = 2,
}

/// <summary>What the caller asked the directory for.</summary>
/// <param name="Search">
/// Matched against legal name, trading name and slug, case-insensitively. Null or blank matches
/// everything.
/// </param>
/// <param name="Status">Only agencies in this status. Null for every status.</param>
/// <param name="Type">Principal or SubAgent. Null for both.</param>
public sealed record AgencyDirectoryQuery(
    string? Search = null,
    AgencyStatus? Status = null,
    AgencyType? Type = null,
    AgencySort Sort = AgencySort.Newest,
    int Page = 1,
    int PageSize = 25)
{
    /// <summary>Enough rows to scan, few enough that one query stays cheap.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page number, never below one however the query string was written.</summary>
    public int SafePage => Page < 1 ? 1 : Page;

    /// <summary>The page size, clamped so a hand-edited URL cannot ask for every agency at once.</summary>
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <summary>
/// Reading the platform's agencies: the directory, and one agency's profile.
/// </summary>
/// <remarks>
/// <para>
/// Every read here crosses agencies, which is what <see cref="IPlatformScope"/> is for — entered
/// explicitly, with a reason, and logged. Never <c>IgnoreQueryFilters</c>: that is a silent
/// per-query escape hatch, and analyser TRIPS002 fails the build on it.
/// </para>
/// <para>
/// The endpoints on top also require a permission, so the scope is never the only thing between
/// an agency and the whole platform's customer list.
/// </para>
/// </remarks>
public sealed class AgencyDirectoryService
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;

    public AgencyDirectoryService(IAppDbContext db, IPlatformScope platformScope)
    {
        _db = db;
        _platformScope = platformScope;
    }

    /// <summary>One page of the directory, with the total behind the filter.</summary>
    public async Task<AgencyDirectoryResponse> SearchAsync(
        AgencyDirectoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var scope = _platformScope.Enter(
            "Admin console agency directory — lists travel agencies across the whole platform");

        var agencies = Filtered(query);

        // Counted before paging, so the console can say "showing 25 of 214" rather than leaving
        // whoever is searching to guess whether there is more.
        var total = await agencies.CountAsync(cancellationToken);

        var page = await Rows(Sorted(agencies, query.Sort)
                .Skip((query.SafePage - 1) * query.SafePageSize)
                .Take(query.SafePageSize))
            .ToListAsync(cancellationToken);

        return new AgencyDirectoryResponse([.. page.Select(ToSummary)], total, query.SafePage, query.SafePageSize);
    }

    /// <summary>One agency in full, or null if there is no such agency.</summary>
    public async Task<AgencyProfileResponse?> ProfileAsync(
        Guid agencyId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Admin console agency profile — reads one agency's standing, staff and trading history");

        var agency = await _db.Agencies
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == agencyId, cancellationToken);

        if (agency is null)
        {
            return null;
        }

        var parentName = agency.ParentAgencyId is { } parentId
            ? await _db.Agencies.AsNoTracking()
                .Where(parent => parent.Id == parentId)
                .Select(parent => parent.TradingName ?? parent.LegalName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var wallet = await _db.Wallets.AsNoTracking()
            .Where(candidate => candidate.AgencyId == agencyId)
            .Select(candidate => new { candidate.BalanceMinor, candidate.ReservedMinor })
            .FirstOrDefaultAsync(cancellationToken);

        // Placed orders only. A cart abandoned at checkout is not trading history, and counting it
        // would make every agency look busier than it is.
        var gross = await _db.Orders.AsNoTracking()
            .Where(order => order.AgencyId == agencyId && order.PlacedAt != null)
            .Select(order => order.TotalGrossMinor)
            .ToListAsync(cancellationToken);

        var users = await UsersOfAsync(agencyId, cancellationToken);

        var subAgents = await Rows(_db.Agencies.AsNoTracking()
                .Where(child => child.ParentAgencyId == agencyId)
                .OrderBy(child => child.LegalName))
            .ToListAsync(cancellationToken);

        return new AgencyProfileResponse(
            agency.Id,
            agency.TradingName ?? agency.LegalName,
            agency.LegalName,
            agency.TradingName,
            agency.Slug,
            agency.Status.ToString(),
            agency.Type.ToString(),
            agency.CountryCode,
            agency.BaseCurrency,
            agency.Timezone,
            agency.TaxId,
            agency.VatRateBasisPoints,
            agency.ParentAgencyId,
            parentName,
            agency.CreatedAt,
            agency.VerifiedAt,
            agency.OnboardingCompletedAt,
            agency.StatusChangedAt,
            agency.StatusReason,
            AgencyAccess.CanTakeNewBookings(agency.Status),
            AgencyAccess.CanServeStorefront(agency.Status),
            wallet?.BalanceMinor.AmountMinor,
            wallet?.ReservedMinor.AmountMinor,
            gross.Count,
            Total(gross),
            users,
            [.. subAgents.Select(ToSummary)]);
    }

    /// <summary>The staff at one agency, with the roles they hold there.</summary>
    private async Task<List<AgencyUserResponse>> UsersOfAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(user => user.AgencyId == agencyId)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Status,
                user.LastLoginAt,
            })
            .ToListAsync(cancellationToken);

        var ids = users.Select(user => user.Id).ToList();

        // One query for every user's roles rather than one per user: an agency with thirty staff
        // would otherwise be thirty round trips for a screen nobody is waiting on.
        var roles = await (
            from grant in _db.UserRoles.AsNoTracking()
            join role in _db.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where ids.Contains(grant.UserId) && grant.AgencyId == agencyId
            select new { grant.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var rolesByUser = roles
            .GroupBy(grant => grant.UserId)
            .ToDictionary(group => group.Key, group => group.Select(grant => grant.Name).Order().ToList());

        return [.. users.Select(user => new AgencyUserResponse(
            user.Id,
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            user.Status.ToString(),
            rolesByUser.TryGetValue(user.Id, out var held) ? held : [],
            user.LastLoginAt))];
    }

    private IQueryable<Agency> Filtered(AgencyDirectoryQuery query)
    {
        var agencies = _db.Agencies.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LIKE over lower-cased columns rather than Npgsql's ILIKE: ILIKE is a provider
            // extension, and Application must not reference Infrastructure — an architecture test
            // fails the build if it does.
            var pattern = $"%{Escape(query.Search.Trim().ToLowerInvariant())}%";

            agencies = agencies.Where(agency =>
                EF.Functions.Like(agency.LegalName.ToLower(), pattern)
                || (agency.TradingName != null && EF.Functions.Like(agency.TradingName.ToLower(), pattern))
                || EF.Functions.Like(agency.Slug, pattern));
        }

        if (query.Status is { } status)
        {
            agencies = agencies.Where(agency => agency.Status == status);
        }

        if (query.Type is { } type)
        {
            agencies = agencies.Where(agency => agency.Type == type);
        }

        return agencies;
    }

    private static IQueryable<Agency> Sorted(IQueryable<Agency> agencies, AgencySort sort) => sort switch
    {
        AgencySort.Oldest => agencies.OrderBy(agency => agency.CreatedAt).ThenBy(agency => agency.Id),
        AgencySort.Name => agencies.OrderBy(agency => agency.LegalName).ThenBy(agency => agency.Id),

        // ThenBy on the id in every case: without a tiebreaker two agencies created in the same
        // millisecond can swap places between pages, and one of them is then never shown.
        _ => agencies.OrderByDescending(agency => agency.CreatedAt).ThenBy(agency => agency.Id),
    };

    /// <summary>
    /// The directory row, read in one query.
    /// </summary>
    /// <remarks>
    /// Projected to this intermediate rather than straight to the contract because the wallet
    /// balance is a <see cref="Money"/>, and a value-converted type has to come back as itself
    /// before anything reads the long inside it. The parent name, balance and order count are
    /// correlated subqueries rather than joins, so an agency with no wallet — which is every
    /// agency before KYB — still appears, with nulls where the numbers would be.
    /// </remarks>
    private IQueryable<DirectoryRow> Rows(IQueryable<Agency> agencies) =>
        agencies.Select(agency => new DirectoryRow(
            agency.Id,
            agency.TradingName ?? agency.LegalName,
            agency.LegalName,
            agency.Slug,
            agency.Status.ToString(),
            agency.Type.ToString(),
            agency.CountryCode,
            agency.BaseCurrency,
            agency.ParentAgencyId,
            _db.Agencies
                .Where(parent => parent.Id == agency.ParentAgencyId)
                .Select(parent => parent.TradingName ?? parent.LegalName)
                .FirstOrDefault(),
            agency.CreatedAt,
            agency.VerifiedAt,
            _db.Wallets
                .Where(wallet => wallet.AgencyId == agency.Id)
                .Select(wallet => (Money?)wallet.BalanceMinor)
                .FirstOrDefault(),
            _db.Orders.Count(order => order.AgencyId == agency.Id && order.PlacedAt != null)));

    private static AgencySummaryResponse ToSummary(DirectoryRow row) =>
        new(row.Id,
            row.Name,
            row.LegalName,
            row.Slug,
            row.Status,
            row.Type,
            row.CountryCode,
            row.BaseCurrency,
            row.ParentAgencyId,
            row.ParentAgencyName,
            row.CreatedAt,
            row.VerifiedAt,
            row.WalletBalance?.AmountMinor,
            row.OrderCount);

    /// <summary>
    /// Escapes the wildcards, so a search for "50% off travel" does not match every agency.
    /// </summary>
    private static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static long Total(IReadOnlyCollection<Money> amounts) =>
        amounts.Aggregate(0L, (running, amount) => checked(running + amount.AmountMinor));

    private sealed record DirectoryRow(
        Guid Id,
        string Name,
        string LegalName,
        string Slug,
        string Status,
        string Type,
        string CountryCode,
        string BaseCurrency,
        Guid? ParentAgencyId,
        string? ParentAgencyName,
        DateTimeOffset CreatedAt,
        DateTimeOffset? VerifiedAt,
        Money? WalletBalance,
        int OrderCount);
}
