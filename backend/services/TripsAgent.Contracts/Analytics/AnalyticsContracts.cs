using System.Text.Json.Serialization;

namespace TripsAgent.Contracts.Analytics;

/// <summary>
/// One day of an agency's trading.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Day"/> is a Lagos calendar day, which is the day the agent means. Every money figure
/// is minor units — kobo — as a <c>long</c>, so a spreadsheet and a ledger can be reconciled against
/// each other without a rounding argument.
/// </para>
/// <para>
/// The three margin figures are nullable and omitted from the JSON when the caller does not hold
/// <c>margin.view</c>. Absent rather than zero: a zero is a number somebody will believe, and
/// "this agency made no margin today" is a very different statement from "you may not see this".
/// </para>
/// </remarks>
public sealed record AgencyDayResponse(
    DateOnly Day,
    string Currency,
    int Orders,
    int Bookings,
    long GrossSalesMinor,
    int Refunds,
    long RefundedGrossMinor,
    int Cancellations,
    int Failures,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NetCostMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarkupMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarginMinor);

/// <summary>The window's totals, and what the window before it did.</summary>
/// <param name="ChangeBasisPoints">
/// How gross sales moved against the previous window of the same length, in basis points — 10,000
/// is a doubling, -5,000 is a halving. Basis points rather than a percentage because a percentage
/// invites a floating-point division somewhere, and integers do not lie about thirds. Null when the
/// previous window sold nothing, since there is no meaningful change from zero.
/// </param>
public sealed record AnalyticsTotalsResponse(
    string Currency,
    int Orders,
    int Bookings,
    long GrossSalesMinor,
    int Refunds,
    long RefundedGrossMinor,
    int Cancellations,
    int Failures,
    long PreviousGrossSalesMinor,
    int? ChangeBasisPoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NetCostMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarkupMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarginMinor);

/// <summary>How a window split across what was sold.</summary>
public sealed record AnalyticsBreakdownResponse(
    string Label,
    int Bookings,
    long GrossSalesMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarginMinor);

/// <summary>
/// An agency's own sales, revenue and margin over a window.
/// </summary>
/// <param name="GeneratedAt">When the read models these came from were last rebuilt, not now.</param>
/// <param name="ShowsMargin">
/// Whether the margin figures are present. The console reads this rather than guessing from a null,
/// so a screen can say "ask an owner" instead of silently dropping a column.
/// </param>
public sealed record AgencyAnalyticsResponse(
    DateOnly From,
    DateOnly To,
    DateTimeOffset? GeneratedAt,
    bool ShowsMargin,
    AnalyticsTotalsResponse Totals,
    IReadOnlyList<AgencyDayResponse> Days,
    IReadOnlyList<AnalyticsBreakdownResponse> ByProductType,
    IReadOnlyList<AnalyticsBreakdownResponse> ByChannel);

/// <summary>One booking behind an aggregate — the drill-down's row.</summary>
/// <remarks>
/// The point of a drill-down is that a number on a dashboard can be taken apart until it is a list
/// of real bookings somebody can open. These rows come from <c>fact_bookings</c>, not from
/// <c>orders</c>, so the total of the rows is the number that was clicked on by construction.
/// </remarks>
public sealed record BookingRowResponse(
    Guid OrderId,
    Guid OrderLineId,
    string OrderNumber,
    DateOnly Day,
    DateTimeOffset OccurredAt,
    string ItemType,
    string Channel,
    string Title,
    string OrderStatus,
    string FulfilmentStatus,
    string Currency,
    long GrossAmountMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NetAmountMinor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MarginMinor);

/// <summary>The transactions behind one aggregate, a page at a time.</summary>
public sealed record BookingDrillDownResponse(
    DateOnly From,
    DateOnly To,
    bool ShowsMargin,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<BookingRowResponse> Rows);

/// <summary>One day of the whole platform.</summary>
public sealed record PlatformDayResponse(
    DateOnly Day,
    string Currency,
    int SellingAgencies,
    int NewAgencies,
    int Orders,
    int Bookings,
    long GmvMinor,
    long PlatformFeeMinor,
    long MarkupMinor,
    int Refunds,
    long RefundedGrossMinor,
    int Failures);

/// <summary>
/// Platform GMV and growth over a window.
/// </summary>
/// <param name="GmvMinor">What travellers paid across every agency. Not Trips' revenue.</param>
/// <param name="PlatformFeeMinor">Trips' revenue: the fee taken from agents' margins.</param>
/// <param name="GmvChangeBasisPoints">
/// GMV against the previous window of the same length, in basis points. Null when the previous
/// window sold nothing.
/// </param>
public sealed record PlatformAnalyticsResponse(
    DateOnly From,
    DateOnly To,
    DateTimeOffset? GeneratedAt,
    string Currency,
    long GmvMinor,
    long PreviousGmvMinor,
    int? GmvChangeBasisPoints,
    long PlatformFeeMinor,
    long MarkupMinor,
    int Orders,
    int Bookings,
    int NewAgencies,
    int ActiveAgencies,
    int Refunds,
    long RefundedGrossMinor,
    int Failures,
    IReadOnlyList<PlatformDayResponse> Days);

/// <summary>
/// How one supplier behaved over a window.
/// </summary>
/// <param name="ConversionBasisPoints">
/// Issued tickets against searches, in basis points: 250 is 2.5%. Null when nobody searched, which
/// is not a conversion of zero — there was nothing to convert.
/// </param>
/// <param name="ErrorRateBasisPoints">
/// Failed calls against all calls, in basis points. Null when no calls were made.
/// </param>
/// <param name="Timeouts">
/// Counted apart from errors on purpose. A timed-out ticket issue is an <i>unknown</i> outcome, not
/// a failure — a ticket may exist — and the answer is always to poll, never to send it again
/// (ADR-0003).
/// </param>
public sealed record SupplierPerformanceRowResponse(
    Guid SupplierId,
    string SupplierCode,
    string SupplierName,
    int Searches,
    int PriceConfirmations,
    int IssueAttempts,
    int Booked,
    int StatusPolls,
    int TotalCalls,
    int Errors,
    int Timeouts,
    int? ConversionBasisPoints,
    int? ErrorRateBasisPoints,
    int AverageLatencyMs,
    int MaxLatencyMs);

/// <summary>Every supplier over a window, plus the daily series for the one being looked at.</summary>
public sealed record SupplierPerformanceResponse(
    DateOnly From,
    DateOnly To,
    DateTimeOffset? GeneratedAt,
    IReadOnlyList<SupplierPerformanceRowResponse> Suppliers,
    IReadOnlyList<SupplierDayResponse> Days);

/// <summary>One supplier's day, for the trend line.</summary>
public sealed record SupplierDayResponse(
    DateOnly Day,
    Guid SupplierId,
    int Searches,
    int Booked,
    int TotalCalls,
    int Errors,
    int AverageLatencyMs);
