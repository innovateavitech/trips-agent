using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// <c>analytics.agg_agency_daily</c> — one agency's day, in one row.
/// </summary>
/// <remarks>
/// <para>
/// Grain: <c>(agency_id, day, currency)</c>. Currency is part of the key rather than converted
/// away, because there is no exchange rate anywhere in this system and inventing one to make a
/// tidier table would be inventing revenue. Each agency sells in its own base currency only
/// (MVP decision 17), so in practice an agency has one row per day.
/// </para>
/// <para>
/// Every money column is a <see cref="Money"/> over a <c>bigint</c> of minor units, summed with
/// integer arithmetic. A day's takings are added up in kobo and stay in kobo; nothing here is ever
/// divided, averaged or held in a floating type, so the total of the days equals the total of the
/// rows and both equal what the ledger says.
/// </para>
/// </remarks>
public sealed class AgencyDailyAggregate : Entity, ITenantScoped
{
    private AgencyDailyAggregate()
    {
        Currency = string.Empty;
    }

    public static AgencyDailyAggregate For(
        Guid agencyId,
        Guid rootAgencyId,
        DateOnly day,
        string currency,
        int ordersCount,
        int bookingsCount,
        Money grossSalesMinor,
        Money netCostMinor,
        Money markupMinor,
        Money taxMinor,
        Money platformFeeMinor,
        int refundedCount,
        Money refundedGrossMinor,
        int cancelledCount,
        int failedCount,
        DateTimeOffset builtAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new AgencyDailyAggregate
        {
            AgencyId = agencyId,
            RootAgencyId = rootAgencyId,
            Day = day,
            Currency = currency,
            OrdersCount = ordersCount,
            BookingsCount = bookingsCount,
            GrossSalesMinor = grossSalesMinor,
            NetCostMinor = netCostMinor,
            MarkupMinor = markupMinor,
            TaxMinor = taxMinor,
            PlatformFeeMinor = platformFeeMinor,
            RefundedCount = refundedCount,
            RefundedGrossMinor = refundedGrossMinor,
            CancelledCount = cancelledCount,
            FailedCount = failedCount,
            BuiltAt = builtAt,
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>The principal at the top of this agency's tree, so a network rolls up in one scan.</summary>
    public Guid RootAgencyId { get; private set; }

    /// <summary>The Lagos calendar day. See <see cref="LagosDay"/>.</summary>
    public DateOnly Day { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Distinct orders with at least one selling line on this day.</summary>
    public int OrdersCount { get; private set; }

    /// <summary>Selling lines — the number an agent thinks of as "bookings".</summary>
    public int BookingsCount { get; private set; }

    /// <summary>What travellers were charged. The agency's top line.</summary>
    public Money GrossSalesMinor { get; private set; }

    /// <summary>What Trips charged for it. Margin — <c>margin.view</c> only.</summary>
    public Money NetCostMinor { get; private set; }

    /// <summary>What the agency added on top. Margin — <c>margin.view</c> only.</summary>
    public Money MarkupMinor { get; private set; }

    public Money TaxMinor { get; private set; }

    /// <summary>The platform's cut, taken out of the markup. Margin — <c>margin.view</c> only.</summary>
    public Money PlatformFeeMinor { get; private set; }

    public int RefundedCount { get; private set; }

    public Money RefundedGrossMinor { get; private set; }

    public int CancelledCount { get; private set; }

    /// <summary>Lines paid for and not delivered. What the resolution queue is holding.</summary>
    public int FailedCount { get; private set; }

    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>What the agency actually keeps. Margin — <c>margin.view</c> only.</summary>
    public Money MarginMinor => MarkupMinor - PlatformFeeMinor;
}

/// <summary>
/// <c>analytics.agg_platform_daily</c> — the whole platform's day, in one row.
/// </summary>
/// <remarks>
/// <para>
/// Grain: <c>(day, currency)</c>. There is no <c>agency_id</c> and there deliberately cannot be
/// one: this table <i>is</i> the cross-tenant view. Because it carries no agency it cannot use the
/// ordinary tenant policy, so its row-level security policy is
/// <c>USING (tenancy.platform_scope_active())</c> — an agency session reads nothing at all from it,
/// not even an empty aggregate it might infer something from. Reading it goes through
/// <c>IPlatformScope.Enter(reason)</c> like every other cross-tenant read, and never
/// <c>IgnoreQueryFilters</c>.
/// </para>
/// <para>
/// <see cref="GmvMinor"/> is gross merchandise value: what travellers paid, in total. It is not
/// Trips' revenue — that is <see cref="PlatformFeeMinor"/>, the cut taken from agents' margins
/// (MVP decision 4). Both are here because the two are constantly confused and a dashboard that
/// shows one labelled as the other is worse than no dashboard.
/// </para>
/// </remarks>
public sealed class PlatformDailyAggregate : Entity
{
    private PlatformDailyAggregate()
    {
        Currency = string.Empty;
    }

    public static PlatformDailyAggregate For(
        DateOnly day,
        string currency,
        int sellingAgenciesCount,
        int newAgenciesCount,
        int ordersCount,
        int bookingsCount,
        Money gmvMinor,
        Money netCostMinor,
        Money markupMinor,
        Money taxMinor,
        Money platformFeeMinor,
        int refundedCount,
        Money refundedGrossMinor,
        int cancelledCount,
        int failedCount,
        DateTimeOffset builtAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new PlatformDailyAggregate
        {
            Day = day,
            Currency = currency,
            SellingAgenciesCount = sellingAgenciesCount,
            NewAgenciesCount = newAgenciesCount,
            OrdersCount = ordersCount,
            BookingsCount = bookingsCount,
            GmvMinor = gmvMinor,
            NetCostMinor = netCostMinor,
            MarkupMinor = markupMinor,
            TaxMinor = taxMinor,
            PlatformFeeMinor = platformFeeMinor,
            RefundedCount = refundedCount,
            RefundedGrossMinor = refundedGrossMinor,
            CancelledCount = cancelledCount,
            FailedCount = failedCount,
            BuiltAt = builtAt,
        };
    }

    public DateOnly Day { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Agencies that sold something on this day. The platform's activity, not its size.</summary>
    public int SellingAgenciesCount { get; private set; }

    /// <summary>Agencies created on this day. The growth half of "GMV and growth".</summary>
    public int NewAgenciesCount { get; private set; }

    public int OrdersCount { get; private set; }

    public int BookingsCount { get; private set; }

    /// <summary>Gross merchandise value: what travellers paid, across every agency.</summary>
    public Money GmvMinor { get; private set; }

    /// <summary>What the platform paid suppliers for it.</summary>
    public Money NetCostMinor { get; private set; }

    /// <summary>What agents added on top, in total.</summary>
    public Money MarkupMinor { get; private set; }

    public Money TaxMinor { get; private set; }

    /// <summary>Trips' own revenue: the fee taken from agents' margins.</summary>
    public Money PlatformFeeMinor { get; private set; }

    public int RefundedCount { get; private set; }

    public Money RefundedGrossMinor { get; private set; }

    public int CancelledCount { get; private set; }

    public int FailedCount { get; private set; }

    public DateTimeOffset BuiltAt { get; private set; }
}

/// <summary>
/// <c>analytics.agg_supplier_daily</c> — how a supplier behaved on one day.
/// </summary>
/// <remarks>
/// <para>
/// Grain: <c>(day, supplier_id)</c>, built from <c>supplier.supplier_api_calls</c> rather than
/// from orders. The FRD's supplier-performance report asks two questions of it: how often a search
/// turns into a ticket, and how often the supplier fails. Both are ratios, and neither is stored —
/// only the counts they are computed from are, so there is no rounded number in the database that
/// somebody later has to reconcile against the counts beside it.
/// </para>
/// <para>
/// Platform-wide, like <see cref="PlatformDailyAggregate"/>: a supplier's error rate is Trips'
/// business with Trips Africa, not an agency's, and one agency must not be able to infer another's
/// volume from it. Same policy, same <c>IPlatformScope</c> requirement.
/// </para>
/// <para>
/// Latency is stored as an average and a maximum in whole milliseconds — integers, because a mean
/// of a million integers held as a float drifts, and because nothing here is money. A percentile
/// would be better and needs an ordered scan of the call log; it can be added when somebody is
/// actually chasing a tail latency.
/// </para>
/// </remarks>
public sealed class SupplierDailyAggregate : Entity
{
    private SupplierDailyAggregate()
    {
    }

    public static SupplierDailyAggregate For(
        DateOnly day,
        Guid supplierId,
        int searchCount,
        int confirmPriceCount,
        int issueCount,
        int statusCount,
        int otherCount,
        int errorCount,
        int timeoutCount,
        int bookedCount,
        int averageLatencyMs,
        int maxLatencyMs,
        DateTimeOffset builtAt) =>
        new()
        {
            Day = day,
            SupplierId = supplierId,
            SearchCount = searchCount,
            ConfirmPriceCount = confirmPriceCount,
            IssueCount = issueCount,
            StatusCount = statusCount,
            OtherCount = otherCount,
            ErrorCount = errorCount,
            TimeoutCount = timeoutCount,
            BookedCount = bookedCount,
            AverageLatencyMs = averageLatencyMs,
            MaxLatencyMs = maxLatencyMs,
            BuiltAt = builtAt,
        };

    public DateOnly Day { get; private set; }

    public Guid SupplierId { get; private set; }

    /// <summary>Search calls. The denominator of search-to-book conversion.</summary>
    public int SearchCount { get; private set; }

    public int ConfirmPriceCount { get; private set; }

    /// <summary>Ticket-issue calls attempted, however they ended.</summary>
    public int IssueCount { get; private set; }

    /// <summary>Booking-status polls. There are no webhooks, so this is the largest number here.</summary>
    public int StatusCount { get; private set; }

    /// <summary>Rules and cancellation calls — everything not counted above.</summary>
    public int OtherCount { get; private set; }

    /// <summary>Calls that ended in an error of any kind, including timeouts.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Calls that timed out. Counted separately because a timeout on the issue call is an
    /// <i>unknown</i> outcome, not a failure (ADR-0003), and reading it as a failure would make the
    /// supplier look worse than it is while hiding the case that actually needs a human.
    /// </summary>
    public int TimeoutCount { get; private set; }

    /// <summary>Issue calls that succeeded. The numerator of search-to-book conversion.</summary>
    public int BookedCount { get; private set; }

    public int AverageLatencyMs { get; private set; }

    public int MaxLatencyMs { get; private set; }

    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>Every call made to this supplier on this day.</summary>
    public int TotalCalls => SearchCount + ConfirmPriceCount + IssueCount + StatusCount + OtherCount;
}
