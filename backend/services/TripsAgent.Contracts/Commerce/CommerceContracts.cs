namespace TripsAgent.Contracts.Commerce;

// The traveller's buying flow (docs/BUILD_PLAN.md F5): the cart on an agency's storefront, guest
// checkout, payment on the gateway's hosted page, and the link that manages the booking afterwards.
//
// Everything here is anonymous and traveller-facing, so two rules hold throughout:
//
//   * Nothing names Trips (CLAUDE.md rule 4). A traveller sees the agency's own name, its own
//     prices and its own site; the platform behind it is the agency's business, not theirs.
//   * Only the sell price is ever shown. Net rate, markup, platform fee and the agency's margin are
//     not in any response here — that is the agent's commercial information, not the buyer's.
//
// Money travels as whole minor units: ₦150,000 is 15000000. Instants are UTC; dates are YYYY-MM-DD.

/// <summary>One thing in the cart, as the traveller sees it.</summary>
/// <param name="ItemType"><c>Flight</c>, <c>Bus</c>, <c>Tour</c>, <c>Visa</c>, <c>GroupDeparture</c> or <c>Package</c>.</param>
/// <param name="Title">What it is, as it read when it went in the cart.</param>
/// <param name="PriceMinor">What this line costs, all in. Indicative until checkout re-prices it.</param>
/// <param name="HoldExpiresAt">When the seats held for this line lapse. Null until checkout holds them.</param>
public sealed record CartItemResponse(
    Guid Id,
    string ItemType,
    string Title,
    int Adults,
    int Children,
    int Infants,
    long PriceMinor,
    string Currency,
    Guid? ProductId,
    Guid? DepartureId,
    DateTimeOffset? HoldExpiresAt);

/// <summary>The cart a traveller is filling.</summary>
/// <param name="SessionToken">
/// What the browser sends back to find this cart again. There are no traveller accounts
/// (decision 21), so this is the only handle on it — keep it, and send it as <c>X-Cart-Session</c>.
/// </param>
/// <param name="TotalMinor">The sum of the lines. Indicative until checkout re-prices them.</param>
/// <param name="ExpiresAt">When an untouched cart is given up on, and any seats it holds go back.</param>
public sealed record CartResponse(
    Guid Id,
    string SessionToken,
    string Currency,
    IReadOnlyList<CartItemResponse> Items,
    long TotalMinor,
    DateTimeOffset ExpiresAt);

/// <summary>Putting something in the cart. Exactly one of the three ids identifies what.</summary>
/// <param name="ProductId">A tour, a visa or a package from the agency's own catalog.</param>
/// <param name="DepartureId">A dated group departure, bought by the seat.</param>
/// <param name="OfferId">A flight or bus fare from a search.</param>
public sealed record AddCartItemRequest(
    Guid? ProductId = null,
    Guid? DepartureId = null,
    Guid? OfferId = null,
    int Adults = 1,
    int Children = 0,
    int Infants = 0);

/// <summary>Who is travelling on one line, for the lines that need names on a ticket.</summary>
/// <param name="Type"><c>Adult</c>, <c>Child</c> or <c>Infant</c>.</param>
/// <param name="PassportNumber">Sent in clear, stored encrypted, and never returned.</param>
public sealed record CheckoutTravellerRequest(
    string Type,
    string FirstName,
    string LastName,
    string? Title = null,
    DateOnly? BirthDate = null,
    string? Gender = null,
    string? Email = null,
    string? Phone = null,
    string? PassportNumber = null,
    DateOnly? PassportExpiry = null,
    string? Nationality = null);

/// <summary>The travellers on one cart line.</summary>
public sealed record CheckoutLineRequest(Guid CartItemId, IReadOnlyList<CheckoutTravellerRequest> Travellers);

/// <summary>How to reach the person buying. The booking, its documents and its link go here.</summary>
public sealed record CheckoutContactRequest(string Name, string Email, string? Phone = null);

/// <summary>Starting a checkout for the cart named by the session token.</summary>
/// <param name="ReturnUrl">
/// Where the gateway sends the traveller when they are done. Must be a page on the agency's own
/// site; anything else is refused, so this cannot be turned into an open redirect.
/// </param>
public sealed record BeginCheckoutRequest(
    CheckoutContactRequest Contact,
    IReadOnlyList<CheckoutLineRequest> Lines,
    string? ReturnUrl = null);

/// <summary>Where to send the traveller to pay, and what they are about to pay.</summary>
/// <param name="Reference">The order's number. What the status endpoint is asked about.</param>
/// <param name="AuthorizationUrl">
/// The gateway's hosted page. Card details are entered there and never reach any server of ours
/// (decision 18, PCI SAQ-A).
/// </param>
/// <param name="AmountDueMinor">
/// What is payable now: the whole order, or the deposit where a departure is sold on a payment plan.
/// </param>
/// <param name="TotalMinor">What the booking comes to in all, deposit or not.</param>
/// <param name="PayBy">
/// The deadline. Past it the fares and seats being held are given back and nothing is charged.
/// </param>
public sealed record BeginCheckoutResponse(
    string Reference,
    string AuthorizationUrl,
    long AmountDueMinor,
    long TotalMinor,
    string Currency,
    DateTimeOffset PayBy);

/// <summary>One line of a booking, as its traveller sees it.</summary>
/// <param name="Status">
/// <c>Pending</c>, <c>Confirmed</c>, <c>NeedsAttention</c>, <c>Cancelled</c> or <c>Refunded</c> —
/// said plainly, and never pretending a line is fine when it is not.
/// </param>
/// <param name="StatusDetail">What is happening with it, in a sentence, when there is more to say.</param>
public sealed record BookingLineResponse(
    Guid Id,
    string ItemType,
    string Title,
    string Status,
    string? StatusDetail,
    long PriceMinor,
    string? Reference);

/// <summary>One instalment on a booking bought with a payment plan.</summary>
/// <param name="Status"><c>Scheduled</c>, <c>Due</c>, <c>Paid</c>, <c>Overdue</c> or <c>Waived</c>.</param>
public sealed record BookingInstalmentResponse(
    int Sequence,
    string Label,
    DateOnly DueDate,
    long AmountMinor,
    string Status);

/// <summary>A document the traveller can download, by a signed link that needs no account.</summary>
public sealed record BookingDocumentResponse(Guid Id, string Kind, string Number, string DownloadUrl);

/// <summary>
/// A booking, as its traveller sees it behind their link.
/// </summary>
/// <param name="Status">
/// <c>AwaitingPayment</c>, <c>Confirmed</c>, <c>PartlyConfirmed</c>, <c>NeedsAttention</c>,
/// <c>Cancelled</c> or <c>Refunded</c>.
/// </param>
/// <param name="AgencyName">Whose booking this is, in their own name. Never ours (rule 4).</param>
public sealed record ManageBookingResponse(
    string Reference,
    string Status,
    string AgencyName,
    string? AgencyEmail,
    string? AgencyPhone,
    DateTimeOffset PlacedAt,
    string Currency,
    long TotalMinor,
    long PaidMinor,
    IReadOnlyList<BookingLineResponse> Lines,
    IReadOnlyList<BookingInstalmentResponse> Instalments,
    IReadOnlyList<BookingDocumentResponse> Documents);

/// <summary>Where a payment has got to, for the page the gateway returns the traveller to.</summary>
/// <param name="Status"><c>pending</c>, <c>paid</c> or <c>failed</c>.</param>
/// <param name="ManageUrl">The traveller's link to their booking, once there is one to show.</param>
public sealed record CheckoutStatusResponse(
    string Reference,
    string Status,
    long AmountDueMinor,
    string Currency,
    string? ManageUrl);
