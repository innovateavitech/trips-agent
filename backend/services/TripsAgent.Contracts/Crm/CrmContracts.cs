namespace TripsAgent.Contracts.Crm;

// The CRM API: the "CRM contract" in docs/BUILD_PLAN.md (F7), which the console's CRM screens are
// built against, field for field. Enum values travel as their PascalCase names — New,
// TripRequestWidget, Draft, Whatsapp — and money as whole minor units: ₦150,000 is 15000000.
// Instants are UTC; dates are YYYY-MM-DD.
//
// Reading needs customer.view; changing anything needs customer.edit. Customers are never keyed in
// first: any inquiry, quote or booking creates or updates one (FRD §2.8 RS-1).

/// <summary>Who a lead or a quote is for.</summary>
public sealed record CustomerRefResponse(Guid Id, string Name, string? Email, string? Phone);

/// <summary>A task's or a message's subject: a lead, a customer or a quote.</summary>
/// <param name="Type"><c>Lead</c>, <c>Customer</c> or <c>Quote</c>.</param>
public sealed record RelatedRecord(string Type, Guid Id);

/// <summary>A lead as a card on the pipeline board.</summary>
/// <param name="Source"><c>TripRequestWidget</c>, <c>ContactForm</c> or <c>Manual</c>.</param>
/// <param name="BudgetMaxMinor">The highest figure the customer gave, in minor units.</param>
/// <param name="Stage"><c>New</c>, <c>Quoted</c>, <c>Negotiating</c>, <c>Won</c> or <c>Lost</c>.</param>
/// <param name="OwnerName">Who is looking after it. Null for a storefront lead nobody has picked up.</param>
/// <param name="NextTaskDueAt">The soonest open task on the lead or any of its quotes.</param>
public sealed record LeadSummaryResponse(
    Guid Id,
    CustomerRefResponse Customer,
    string Source,
    string Destination,
    DateOnly? TravelFrom,
    DateOnly? TravelTo,
    int Adults,
    int Children,
    long? BudgetMaxMinor,
    string Currency,
    string Stage,
    string? OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextTaskDueAt,
    int QuoteCount);

/// <summary>One move of a lead, oldest first in <see cref="LeadResponse.History"/>.</summary>
/// <param name="ByName">Who moved it, or <c>Website</c> for a lead the storefront opened.</param>
/// <param name="Reason">Why, for a move to Lost.</param>
public sealed record StageChangeResponse(string Stage, DateTimeOffset At, string ByName, string? Reason);

/// <summary>What a task is about, with a label to show for it.</summary>
/// <param name="Label">"Chiamaka Okonkwo · Dubai" for a lead, "QT-0007 · Adeola Martins" for a quote, the name for a customer.</param>
public sealed record TaskRelatedResponse(string Type, Guid Id, string Label);

/// <summary>A follow-up task.</summary>
/// <param name="CompletedAt">When it was done. Null while it is open.</param>
public sealed record TaskResponse(
    Guid Id,
    string Title,
    DateTimeOffset DueAt,
    DateTimeOffset? CompletedAt,
    TaskRelatedResponse Related,
    string? OwnerName);

/// <summary>One entry on a timeline, newest first.</summary>
/// <param name="Channel"><c>Email</c>, <c>Sms</c>, <c>Whatsapp</c>, <c>Call</c> or <c>Note</c>.</param>
/// <param name="Direction"><c>Inbound</c> or <c>Outbound</c>.</param>
/// <param name="ByName">The customer for an inbound message; the person at the agency for an outbound one.</param>
public sealed record CommunicationResponse(
    Guid Id,
    string Channel,
    string Direction,
    string Summary,
    DateTimeOffset At,
    string ByName,
    RelatedRecord Related);

/// <summary>A quote as a line in a list.</summary>
/// <param name="Status">
/// <c>Draft</c>, <c>Sent</c>, <c>Viewed</c>, <c>Accepted</c>, <c>Declined</c> or <c>Expired</c>. A sent
/// quote past its last valid day reads as Expired.
/// </param>
public sealed record QuoteSummaryResponse(
    Guid Id,
    string QuoteNumber,
    string Title,
    string Status,
    long TotalMinor,
    string Currency,
    DateOnly ValidUntil,
    DateTimeOffset? SentAt);

/// <summary>A whole lead: the card, plus what was asked, its history, quotes, tasks and messages.</summary>
/// <param name="Tasks">The lead's own tasks and its quotes' tasks, soonest due first.</param>
/// <param name="Communications">Messages about the lead and its quotes, newest first.</param>
public sealed record LeadResponse(
    Guid Id,
    CustomerRefResponse Customer,
    string Source,
    string Destination,
    DateOnly? TravelFrom,
    DateOnly? TravelTo,
    int Adults,
    int Children,
    long? BudgetMaxMinor,
    string Currency,
    string Stage,
    string? OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextTaskDueAt,
    int QuoteCount,
    string Message,
    long? BudgetMinMinor,
    string? LostReason,
    IReadOnlyList<StageChangeResponse> History,
    IReadOnlyList<QuoteSummaryResponse> Quotes,
    IReadOnlyList<TaskResponse> Tasks,
    IReadOnlyList<CommunicationResponse> Communications);

/// <summary>Who the lead is for. Found by email, then phone; created when neither matches.</summary>
/// <param name="Email">Either this or <paramref name="Phone"/> is needed.</param>
public sealed record LeadCustomerRequest(string Name, string? Email, string? Phone);

/// <summary>A lead the agent keys in: a call, a walk-in, a message. Its source is <c>Manual</c>.</summary>
public sealed record LeadRequest(
    LeadCustomerRequest Customer,
    string Destination,
    DateOnly? TravelFrom,
    DateOnly? TravelTo,
    int Adults,
    int Children,
    long? BudgetMinMinor,
    long? BudgetMaxMinor,
    string Message);

/// <summary>Moves a lead along the pipeline.</summary>
/// <param name="Reason">Required for <c>Lost</c>, and kept only for it.</param>
public sealed record MoveLeadRequest(string Stage, string? Reason);

/// <summary>One priced line.</summary>
/// <param name="UnitPriceMinor">The price of one, in minor units.</param>
/// <param name="ProductId">One of the agency's own catalog products, or null.</param>
public sealed record QuoteItemRequest(string Description, int Quantity, long UnitPriceMinor, Guid? ProductId);

/// <summary>One proposed day. The n-th day in the list is day n.</summary>
public sealed record QuoteDayRequest(int DayNumber, string Title, string Description);

/// <summary>A whole draft quote, to create or to save. A save replaces the items and days.</summary>
/// <param name="ValidUntil">The last day it can be accepted, inclusive. Not in the past.</param>
public sealed record QuoteRequest(
    string Title,
    DateOnly ValidUntil,
    IReadOnlyList<QuoteItemRequest> Items,
    IReadOnlyList<QuoteDayRequest> Itinerary,
    string Notes);

/// <summary>One priced line.</summary>
public sealed record QuoteItemResponse(string Description, int Quantity, long UnitPriceMinor, Guid? ProductId);

/// <summary>One proposed day.</summary>
public sealed record QuoteDayResponse(int DayNumber, string Title, string Description);

/// <summary>A whole quote.</summary>
/// <param name="PublicUrl">The customer's link, on the agency's own storefront. Null until it is sent.</param>
/// <param name="ViewedAt">The first time the customer opened the link.</param>
/// <param name="RespondedAt">When the customer accepted or declined it.</param>
public sealed record QuoteResponse(
    Guid Id,
    string QuoteNumber,
    Guid LeadId,
    CustomerRefResponse Customer,
    string Title,
    string Status,
    DateOnly ValidUntil,
    string Currency,
    IReadOnlyList<QuoteItemResponse> Items,
    IReadOnlyList<QuoteDayResponse> Itinerary,
    string Notes,
    long TotalMinor,
    string? PublicUrl,
    DateTimeOffset? SentAt,
    DateTimeOffset? ViewedAt,
    DateTimeOffset? RespondedAt);

/// <summary>A customer as a row of the list.</summary>
/// <param name="LifetimeValueMinor">What their paid bookings came to — cancelled and refunded ones excluded.</param>
/// <param name="TotalBookings">How many paid bookings they have made, on the same terms.</param>
/// <param name="OpenLeadCount">Leads not yet Won or Lost.</param>
public sealed record CustomerSummaryResponse(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    long LifetimeValueMinor,
    int TotalBookings,
    DateTimeOffset LastActivityAt,
    int OpenLeadCount);

/// <summary>One of a customer's bookings.</summary>
/// <param name="Reference">The order number.</param>
/// <param name="TravelDate">The first departure, where the booking has one on record.</param>
/// <param name="Status">As the agent reads it: "Confirmed", "Awaiting payment", "Refunded".</param>
/// <param name="AmountMinor">What the customer pays for it, in minor units.</param>
public sealed record CustomerBookingResponse(
    string Reference,
    string Title,
    DateOnly? TravelDate,
    string Status,
    long AmountMinor);

/// <summary>The customer 360: who they are, and everything they have asked, been quoted and booked.</summary>
public sealed record CustomerResponse(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    long LifetimeValueMinor,
    int TotalBookings,
    DateTimeOffset LastActivityAt,
    int OpenLeadCount,
    string Currency,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LeadSummaryResponse> Leads,
    IReadOnlyList<QuoteSummaryResponse> Quotes,
    IReadOnlyList<CustomerBookingResponse> Bookings,
    IReadOnlyList<TaskResponse> Tasks,
    IReadOnlyList<CommunicationResponse> Communications);

/// <summary>A follow-up task. Its owner is the person adding it, and they are reminded as it falls due.</summary>
public sealed record TaskRequest(string Title, DateTimeOffset DueAt, RelatedRecord Related);

/// <summary>A message to put on the timeline. Nothing is sent: SMS and WhatsApp are logged, not sent.</summary>
public sealed record CommunicationRequest(string Channel, string Direction, string Summary, RelatedRecord Related);

// ---------------------------------------------------------------------------------- the storefront
//
// Anonymous. The agency is the one whose storefront answers on the host name the traveller used,
// sent as X-Storefront-Host (the request's own Host is used when it is absent). Nothing here may
// mention Trips (CLAUDE.md rule 4).

/// <summary>The storefront's trip-request form. Creates a New lead with source <c>TripRequestWidget</c>.</summary>
/// <param name="Email">Either this or <paramref name="Phone"/> is needed.</param>
public sealed record TripRequestSubmission(
    string Name,
    string? Email,
    string? Phone,
    string Destination,
    DateOnly? TravelFrom,
    DateOnly? TravelTo,
    int Adults,
    int Children,
    long? BudgetMinMinor,
    long? BudgetMaxMinor,
    string Message);

/// <summary>A quote as its customer sees it on the storefront.</summary>
/// <param name="Status"><c>Sent</c>, <c>Viewed</c>, <c>Accepted</c>, <c>Declined</c> or <c>Expired</c>.</param>
/// <param name="CanRespond">True while it can still be accepted or declined.</param>
public sealed record PublicQuoteResponse(
    string QuoteNumber,
    string Title,
    string Status,
    DateOnly ValidUntil,
    string Currency,
    IReadOnlyList<QuoteItemResponse> Items,
    IReadOnlyList<QuoteDayResponse> Itinerary,
    string Notes,
    long TotalMinor,
    string CustomerName,
    DateTimeOffset SentAt,
    DateTimeOffset? RespondedAt,
    bool CanRespond);

/// <summary>Declining a quote, with the customer's reason if they give one.</summary>
public sealed record DeclineQuoteRequest(string? Reason);
