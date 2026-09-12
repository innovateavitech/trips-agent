using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>What one agency in the network sold over a date range.</summary>
/// <param name="MarginMinor">
/// Markup less the platform's fee, in kobo — <b>null for a caller without <c>margin.view</c></b>,
/// and left out of the JSON entirely rather than sent as null. See <c>SubAgentEndpoints</c>.
/// </param>
public sealed record NetworkMemberPerformance(
    Guid AgencyId,
    string Name,
    AgencyStatus Status,
    bool IsPrincipal,
    int Orders,
    long SalesMinor,
    long? MarginMinor,
    long? AllowanceSpentMinor,
    long? AllowanceLimitMinor);

/// <summary>The principal's whole network over a date range, and each member's part of it.</summary>
public sealed record NetworkPerformance(
    DateTimeOffset From,
    DateTimeOffset To,
    string Currency,
    int Orders,
    long SalesMinor,
    long? MarginMinor,
    IReadOnlyList<NetworkMemberPerformance> Members);

/// <summary>
/// Consolidated reporting across a principal's network.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it crosses the network line.</b> It does not: <c>Order</c>'s tenant filter is
/// <c>agency_id = me</c>, so this reads each member's orders by asking for the ids of the
/// agencies the caller already owns, under one audited <see cref="IPlatformScope"/>. That is the
/// deliberate choice for this one read — a principal seeing its sub-agents' <i>money</i> is the
/// one place in the feature where an unlogged cross-tenant read would be worth regretting, and
/// the scope's reason string is what makes it reviewable afterwards.
/// </para>
/// <para>
/// Everywhere else in this feature the hierarchy is an explicit filter rather than a scope, because
/// the rows are the network's own — see <c>AppDbContext.ApplyTenantQueryFilters</c>.
/// </para>
/// <para>
/// <b>It sums orders, not a read model.</b> The analytics read models are feature F11 and do not
/// exist yet. The shape of this service does not change when they do: the same method, reading
/// <c>agg_agency_daily</c> instead. Until then the sums are bounded by a date range the endpoint
/// caps at a year.
/// </para>
/// <para>
/// <b>A sub-agent calling it sees only itself.</b> The member list is built from the caller's own
/// agency when it has no children, so the same endpoint serves both without a second code path.
/// </para>
/// </remarks>
public sealed class SubAgentNetworkReport
{
    /// <summary>The longest range one request may ask for. Beyond this is F11's asynchronous export.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPlatformScope _platformScope;

    public SubAgentNetworkReport(IAppDbContext db, ITenantContext tenant, IPlatformScope platformScope)
    {
        _db = db;
        _tenant = tenant;
        _platformScope = platformScope;
    }

    /// <param name="includeMargin">
    /// Whether the caller holds <c>margin.view</c>. False leaves every margin figure null, and the
    /// endpoint then serialises a response type that has no margin property at all.
    /// </param>
    public async Task<NetworkPerformance> RunAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        bool includeMargin,
        CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId
            ?? throw new SubAgentRefusedException(
                SubAgentRefusal.Forbidden, "This request has no agency, so it has no network.");

        if (to <= from)
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid, "The end of the range has to come after its start.");
        }

        if (to - from > MaxRange)
        {
            throw new SubAgentRefusedException(
                SubAgentRefusal.Invalid,
                "That range is longer than a year.",
                "Ask for a shorter one, or export it once scheduled reports exist.");
        }

        // Npgsql refuses a non-UTC offset, and a range built from a browser's clock arrives with one.
        var start = from.ToUniversalTime();
        var end = to.ToUniversalTime();

        // "Me, and anything whose parent is me" — the Agency filter's own rule, so a sub-agent
        // calling this gets a list of one and sees nobody else.
        var members = await _db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == agencyId || agency.ParentAgencyId == agencyId)
            .Select(agency => new
            {
                agency.Id,
                Name = agency.TradingName ?? agency.LegalName,
                agency.Status,
                agency.ParentAgencyId,
                agency.BaseCurrency,
            })
            .ToListAsync(cancellationToken);

        var ids = members.Select(member => member.Id).ToList();

        var allowances = await _db.WalletAllowances
            .AsNoTracking()
            .Where(allowance => ids.Contains(allowance.SubAgencyId))
            .Select(allowance => new
            {
                allowance.SubAgencyId,
                allowance.SpentMinor,
                allowance.LimitMinor,
            })
            .ToListAsync(cancellationToken);

        List<OrderTotals> totals;

        using (_platformScope.Enter(
            "sub-agent network reporting — a principal's consolidated figures include its sub-agents' orders"))
        {
            // The agency ids are repeated in the predicate on purpose: under the scope the filter
            // is off, and this WHERE is then the only thing keeping the read to this network.
            //
            // Summed here rather than in PostgreSQL: money is a Money value object behind a value
            // converter, and SUM over a converted type is not something EF can translate. The rows
            // are one date range of one network's orders, and the shape of this method does not
            // change when F11's daily read models arrive — it reads agg_agency_daily instead, and
            // the sums go back to the database where they belong.
            var placed = await _db.Orders
                .AsNoTracking()
                .Where(order => ids.Contains(order.AgencyId)
                                && order.PlacedAt != null
                                && order.PlacedAt >= start
                                && order.PlacedAt < end
                                && order.Status != OrderStatus.Cancelled)
                .Select(order => new
                {
                    order.AgencyId,
                    order.TotalGrossMinor,
                    order.TotalMarkupMinor,
                    order.TotalPlatformFeeMinor,
                })
                .ToListAsync(cancellationToken);

            totals = placed
                .GroupBy(order => order.AgencyId)
                .Select(group => new OrderTotals(
                    group.Key,
                    group.Count(),
                    group.Sum(order => order.TotalGrossMinor.AmountMinor),
                    group.Sum(order => order.TotalMarkupMinor.AmountMinor - order.TotalPlatformFeeMinor.AmountMinor)))
                .ToList();
        }

        var rows = members
            .OrderByDescending(member => member.ParentAgencyId == null)
            .ThenBy(member => member.Name, StringComparer.Ordinal)
            .Select(member =>
            {
                var sold = totals.Find(candidate => candidate.AgencyId == member.Id);
                var allowance = allowances.Find(candidate => candidate.SubAgencyId == member.Id);

                return new NetworkMemberPerformance(
                    member.Id,
                    member.Name,
                    member.Status,
                    member.ParentAgencyId is null,
                    sold?.Orders ?? 0,
                    sold?.SalesMinor ?? 0,
                    includeMargin ? sold?.MarginMinor ?? 0 : null,
                    allowance?.SpentMinor.AmountMinor,
                    allowance?.LimitMinor.AmountMinor);
            })
            .ToList();

        return new NetworkPerformance(
            start,
            end,
            members.Find(member => member.Id == agencyId)?.BaseCurrency ?? "NGN",
            rows.Sum(row => row.Orders),
            rows.Sum(row => row.SalesMinor),
            includeMargin ? rows.Sum(row => row.MarginMinor ?? 0) : null,
            rows);
    }

    private sealed record OrderTotals(Guid AgencyId, int Orders, long SalesMinor, long MarginMinor);
}
