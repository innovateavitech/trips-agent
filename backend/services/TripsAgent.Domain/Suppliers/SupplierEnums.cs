namespace TripsAgent.Domain.Suppliers;

/// <summary>What a supplier sells. Stored on <c>suppliers.kind</c>.</summary>
public enum SupplierKind
{
    Flight = 1,
    Bus = 2,

    /// <summary>More than one product line — Trips Africa sells both flights and bus tickets.</summary>
    Multi = 3,
}

/// <summary>
/// The product a search, offer or booking is for, in <b>our</b> vocabulary rather than any one
/// supplier's. Trips Africa calls a bus trip <c>TripMode: "Road"</c>; the next aggregator will call
/// it something else. Both map onto this.
/// </summary>
public enum SupplierProductType
{
    Flight = 1,
    Bus = 2,
}

/// <summary>Which of a supplier's environments a credential belongs to.</summary>
public enum SupplierEnvironment
{
    Staging = 1,
    Production = 2,
}

/// <summary>
/// Where a supplier booking has got to, in our own terms.
/// </summary>
/// <remarks>
/// <para>
/// The supplier's own status code (Trips Africa's 0, 1, 2, 3, 11, 100) is stored beside this in
/// <c>supplier_status_code</c>, verbatim. This enum is the normalised view the checkout saga and
/// the poller act on, so a second aggregator maps its codes onto it without a schema change.
/// </para>
/// <para>
/// <see cref="IssueOutcomeUnknown"/> exists because of ADR-0003: a timeout on the issue call is
/// not a failure. It is resolved by asking the supplier, never by issuing again.
/// </para>
/// </remarks>
public enum SupplierBookingStatus
{
    /// <summary>Created; the price has not been confirmed with the supplier yet.</summary>
    PendingConfirmation = 1,

    /// <summary>Price confirmed and every confirmation's hash verified. Ready to issue.</summary>
    PriceConfirmed = 2,

    /// <summary>A confirmation hash did not verify. Issuing is blocked; this is a security event.</summary>
    PriceRejected = 3,

    /// <summary>The issue call has been sent. Never sent twice — see ADR-0003.</summary>
    Issuing = 4,

    /// <summary>The issue call timed out or its answer was lost. Resolved by polling, never by re-issuing.</summary>
    IssueOutcomeUnknown = 5,

    /// <summary>The supplier accepted the issue request but has not finished ticketing.</summary>
    TicketPending = 6,

    /// <summary>Ticketed. Terminal.</summary>
    Ticketed = 7,

    /// <summary>The supplier refused, or the booking reached a state that needs a reversal. Terminal.</summary>
    Failed = 8,

    /// <summary>Cancelled with the supplier. Terminal.</summary>
    Cancelled = 9,

    /// <summary>The ticket time limit passed before issuing. Terminal.</summary>
    Expired = 10,
}

/// <summary>The passenger categories fares are priced by (IATA ADT / CHD / INF).</summary>
public enum PassengerType
{
    Adult = 1,
    Child = 2,
    Infant = 3,
}

/// <summary>The IATA special-service record a travel document is sent as.</summary>
public enum TravelDocumentRecord
{
    /// <summary>DOCS — the passport or national identity document the passenger travels on.</summary>
    Docs = 1,

    /// <summary>DOCO — other documents, typically a visa.</summary>
    Doco = 2,
}

/// <summary>The kind of document inside a <see cref="TravelDocumentRecord"/>.</summary>
public enum TravelDocumentKind
{
    Passport = 1,
    Visa = 2,
    NationalId = 3,
}

/// <summary>Which adapter operation an audited supplier call was.</summary>
public enum SupplierOperation
{
    Search = 1,
    ConfirmPrice = 2,
    Issue = 3,
    Status = 4,
    Rules = 5,
    Cancel = 6,
}

/// <summary>How an audited supplier call ended, from the transport's point of view.</summary>
/// <remarks>
/// <b>Which outcomes may be sent again.</b> Only <see cref="TransportError"/> proves the supplier never
/// saw the request. <see cref="Timeout"/>, <see cref="Cancelled"/> and <see cref="OutcomeUnknown"/> all
/// mean the request may have reached the supplier and been acted on: for the ticket-issue call, a ticket
/// may exist, so the answer is to poll the booking's status, never to send the call again (ADR-0003).
/// <see cref="Succeeded"/> and <see cref="HttpError"/> are answers, and the caller decides what they mean.
/// </remarks>
public enum SupplierCallOutcome
{
    /// <summary>A response arrived with a success status code.</summary>
    Succeeded = 1,

    /// <summary>A response arrived with an error status code.</summary>
    HttpError = 2,

    /// <summary>
    /// No response in time. The supplier may still have acted on the request: for the issue call this
    /// is an unknown outcome, not a failure. Never safe to send again.
    /// </summary>
    Timeout = 3,

    /// <summary>
    /// The request provably never reached the supplier: the name did not resolve, the connection was
    /// refused, or the TLS handshake or proxy tunnel failed before anything was sent. The only
    /// failure that is safe to send again.
    /// </summary>
    TransportError = 4,

    /// <summary>
    /// The caller stopped waiting before a response arrived — a request aborted, a host shutting
    /// down. Like <see cref="Timeout"/>, the supplier may still have acted on it. Never safe to send again.
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// The call failed after the request may have been sent, and no complete answer came back: the
    /// connection was reset or closed mid-call, the reply was not valid HTTP, or the body was cut off
    /// after the status line. Like <see cref="Timeout"/>, the supplier may have acted on it. Never
    /// safe to send again.
    /// </summary>
    OutcomeUnknown = 6,
}

/// <summary>What one status poll learned.</summary>
public enum SupplierPollOutcome
{
    /// <summary>The supplier answered with a status code.</summary>
    Answered = 1,

    /// <summary>The supplier does not know the booking.</summary>
    NotFound = 2,

    HttpError = 3,

    Timeout = 4,
}

/// <summary>What the poller did about it — the evidence trail for any payment reversal.</summary>
public enum SupplierPollAction
{
    /// <summary>Nothing final yet; polled again later.</summary>
    Rescheduled = 1,

    MarkedTicketed = 2,

    /// <summary>The supplier's documented reversal conditions were met.</summary>
    ReversalRequested = 3,

    /// <summary>Handed to a person through <c>admin_alerts</c>.</summary>
    AlertRaised = 4,

    /// <summary>The ticket time limit passed.</summary>
    Expired = 5,
}
