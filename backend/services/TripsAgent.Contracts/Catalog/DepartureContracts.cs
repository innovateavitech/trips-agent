namespace TripsAgent.Contracts.Catalog;

// The group departures API from issue #57, which the console's departure screens are built
// against. Enum values travel as their PascalCase names — Open, Percent, FromBooking — money as
// whole minor units, and a departure date as a plain date, because a departure leaves on a day
// rather than at a UTC instant. The cutoff is the one instant here.

/// <summary>
/// A whole departure as the console writes it: to create one, or to save one. A save replaces
/// everything — the date, the seats, the prices, the deposit and the installment plan.
/// </summary>
/// <param name="DepartureDate">The day it leaves. Has to be in the future when it is set.</param>
/// <param name="IsGroupDeparture">
/// True means it only runs once <paramref name="MinPax"/> travellers have paid. False means it runs
/// whatever happens, and is guaranteed from the moment it opens.
/// </param>
/// <param name="MinPax">The smallest party that makes it run. Ignored unless it is a group departure.</param>
/// <param name="CapacityTotal">Every seat there is, 1 to 5,000.</param>
/// <param name="CutoffDaysBefore">Bookings close this many days before it leaves.</param>
/// <param name="DepositType"><c>None</c>, <c>Percent</c> or <c>Fixed</c>.</param>
/// <param name="DepositPercentBasisPoints">Set exactly when the deposit is a percentage: 2,500 is 25%.</param>
/// <param name="DepositAmountMinor">Set exactly when the deposit is a fixed amount, in kobo.</param>
/// <param name="PriceTiers">Contiguous from a party of 1, the last one open-ended.</param>
/// <param name="Installments">
/// The balance after the deposit, split into payments whose shares add up to 10,000 basis points.
/// Empty means the whole balance is due at the cutoff.
/// </param>
/// <param name="Version">
/// The version the departure was read at. Required on a save and ignored on a create; a stale one
/// is a 409, so two people cannot silently overwrite each other.
/// </param>
public sealed record DepartureRequest(
    DateOnly DepartureDate,
    bool IsGroupDeparture,
    int MinPax,
    int CapacityTotal,
    int CutoffDaysBefore,
    string DepositType,
    int? DepositPercentBasisPoints,
    long? DepositAmountMinor,
    IReadOnlyList<PriceTierRequest> PriceTiers,
    IReadOnlyList<InstallmentRequest> Installments,
    int Version);

/// <summary>The price per traveller for a party of this size.</summary>
/// <param name="MinPax">The smallest party this price is for. 1 on the first tier.</param>
/// <param name="MaxPax">The largest. Omitted on the last tier: this size and up.</param>
/// <param name="PricePerPaxMinor">In kobo, above zero.</param>
public sealed record PriceTierRequest(int MinPax, int? MaxPax, long PricePerPaxMinor);

/// <summary>One payment of the balance left after the deposit.</summary>
/// <param name="Sequence">1 for the first payment. Renumbered from list order on every save.</param>
/// <param name="DueBasis"><c>FromBooking</c> or <c>BeforeDeparture</c>.</param>
/// <param name="DueOffsetDays">Days after they booked, or days before it leaves.</param>
/// <param name="PercentOfBalanceBasisPoints">A share of the balance: 5,000 is half.</param>
public sealed record InstallmentRequest(
    int Sequence,
    string DueBasis,
    int DueOffsetDays,
    int PercentOfBalanceBasisPoints);

/// <summary>A departure as the console reads it: what was written, plus what the server works out.</summary>
/// <param name="ProductTitle">The tour or package this is a dated run of.</param>
/// <param name="Currency">The product's currency — the agency's own (decision 17).</param>
/// <param name="Status">
/// <c>Open</c>, <c>Guaranteed</c>, <c>NearlyFull</c>, <c>SoldOut</c>, <c>Closed</c> or
/// <c>Cancelled</c>. The server works the first four out from the seats; the last two are the
/// agent's own decision and are never overwritten.
/// </param>
/// <param name="CapacityReserved">Held during checkout, not yet paid.</param>
/// <param name="CapacityConfirmed">Paid for.</param>
/// <param name="SeatsLeft">What is still on sale: total less reserved less confirmed.</param>
/// <param name="WaitlistCount">How many people are waiting or hold an open offer.</param>
/// <param name="CutoffAt">The instant bookings close, in UTC.</param>
/// <param name="Version">Send this back with a save.</param>
public sealed record DepartureResponse(
    Guid Id,
    Guid ProductId,
    string ProductTitle,
    string Currency,
    DateOnly DepartureDate,
    bool IsGroupDeparture,
    int MinPax,
    int CapacityTotal,
    int CutoffDaysBefore,
    string DepositType,
    int? DepositPercentBasisPoints,
    long? DepositAmountMinor,
    IReadOnlyList<PriceTierResponse> PriceTiers,
    IReadOnlyList<InstallmentResponse> Installments,
    string Status,
    int CapacityReserved,
    int CapacityConfirmed,
    int SeatsLeft,
    int WaitlistCount,
    DateTimeOffset CutoffAt,
    int Version);

/// <inheritdoc cref="PriceTierRequest"/>
public sealed record PriceTierResponse(int MinPax, int? MaxPax, long PricePerPaxMinor);

/// <inheritdoc cref="InstallmentRequest"/>
public sealed record InstallmentResponse(
    int Sequence,
    string DueBasis,
    int DueOffsetDays,
    int PercentOfBalanceBasisPoints);

/// <summary>One traveller on the manifest an agent hands the operator on the day.</summary>
/// <param name="OrderReference">The booking they are on.</param>
/// <param name="PaxType"><c>Adult</c>, <c>Child</c> or <c>Infant</c>.</param>
/// <param name="Room">"Room 3", "Twin share". Null until rooms are assigned.</param>
/// <param name="Status"><c>Reserved</c> while the booking is only held, <c>Confirmed</c> once it is paid.</param>
public sealed record ManifestEntryResponse(
    string OrderReference,
    string TravellerName,
    string PaxType,
    string? Room,
    string Status);

/// <summary>Somebody waiting for a seat on a departure that has none.</summary>
/// <param name="Status"><c>Waiting</c>, <c>Offered</c>, <c>Converted</c> or <c>Expired</c>.</param>
/// <param name="JoinedAt">Their place in the queue: the earliest waiting entry is offered first.</param>
/// <param name="OfferedAt">When they were offered a seat, when they have been.</param>
/// <param name="ExpiresAt">When that offer runs out. Null unless one is open.</param>
public sealed record WaitlistEntryResponse(
    Guid Id,
    string Name,
    int PaxCount,
    string Status,
    DateTimeOffset JoinedAt,
    DateTimeOffset? OfferedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Somebody asking to be told when a seat comes up.</summary>
/// <param name="PaxCount">How many seats they want. At least one.</param>
public sealed record JoinWaitlistRequest(string Name, string Email, int PaxCount);
