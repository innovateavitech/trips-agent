namespace TripsAgent.Application.Orders;

/// <summary>The customer an order's documents and emails are for.</summary>
/// <param name="Name">How to greet them, and the name their documents are made out to.</param>
/// <param name="Email">
/// Where their email goes. Null when there is none: documents are still issued, and the agent can
/// download them, but nothing is emailed.
/// </param>
/// <remarks>
/// Passed in by the caller — the checkout saga — rather than looked up, because an order does not
/// record a customer's contact details yet: a guest checkout has no customer record at all.
/// </remarks>
public sealed record BookingCustomer(string Name, string? Email);
