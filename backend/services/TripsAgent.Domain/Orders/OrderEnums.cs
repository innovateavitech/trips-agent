namespace TripsAgent.Domain.Orders;

/// <summary>Where an order is in its life, from the buyer's point of view.</summary>
/// <remarks>
/// The saga (#42) owns the transitions between these; an order created here starts at
/// <see cref="PendingPayment"/>. Stored by name, so reordering this enum cannot silently change
/// what a stored order means.
/// </remarks>
public enum OrderStatus
{
    PendingPayment = 1,
    Paid = 2,
    PartiallyFulfilled = 3,
    Confirmed = 4,
    PartiallyFailed = 5,
    Cancelled = 6,
    Refunded = 7,
}

/// <summary>How an order was paid, which decides where a refund goes.</summary>
public enum OrderPaymentMethod
{
    /// <summary>From the agency's prepaid wallet: held at payment, taken only once ticketed.</summary>
    Wallet = 1,

    /// <summary>On a card, through the payment gateway.</summary>
    Card = 2,
}

/// <summary>Who bought: the traveller on a storefront, or an agent booking on their behalf.</summary>
public enum BuyerType
{
    Customer = 1,
    AgentAssisted = 2,
}

/// <summary>Where the order came from.</summary>
public enum OrderChannel
{
    Storefront = 1,
    Console = 2,
}

/// <summary>What an order line sells. Mirrors the priced product types.</summary>
public enum OrderLineItemType
{
    Flight = 1,
    Bus = 2,
    Tour = 3,
    Visa = 4,
    GroupDeparture = 5,
}

/// <summary>
/// How far a line has got with the supplier.
/// </summary>
/// <remarks>
/// <see cref="FailedNeedsResolution"/> is the one that matters operationally: the money is taken and
/// the supplier did not deliver, so a person has to decide what happens. The index on
/// (agency_id, fulfilment_status) IS the agent's resolution queue (#44).
/// </remarks>
public enum FulfilmentStatus
{
    Pending = 1,
    Reserved = 2,
    Confirming = 3,
    Confirmed = 4,
    FailedNeedsResolution = 5,
    Cancelled = 6,
    Refunded = 7,
}

/// <summary>What is being done about a line that failed.</summary>
public enum ResolutionStatus
{
    Open = 1,
    InProgress = 2,
    ResolvedRebooked = 3,
    ResolvedRefunded = 4,
}

/// <summary>Airline-style passenger types: adult, child, infant.</summary>
public enum TravellerType
{
    Adult = 1,
    Child = 2,
    Infant = 3,
}

/// <summary>Where a cart is in its short life.</summary>
/// <remarks>
/// <see cref="Abandoned"/> means somebody walked away from a cart that was still valid;
/// <see cref="Expired"/> means it timed out. Different marketing follow-ups, so different words.
/// </remarks>
public enum CartStatus
{
    Active = 1,
    Converted = 2,
    Abandoned = 3,
    Expired = 4,
}
