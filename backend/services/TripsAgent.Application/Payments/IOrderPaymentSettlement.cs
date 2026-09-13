namespace TripsAgent.Application.Payments;

/// <summary>
/// Settling a payment that pays for a booking, rather than topping a wallet up.
/// </summary>
/// <remarks>
/// <para>
/// A seam, not an abstraction for its own sake. The webhook handler has to route a confirmed
/// payment by what it was for, and a traveller's booking payment is settled by the commerce side
/// (<c>CustomerOrderPayments</c>) — which knows about carts, orders and suppliers, none of which
/// the payments side has any business knowing about.
/// </para>
/// <para>
/// Implementations must be safe to call repeatedly and concurrently: the gateway's webhook and the
/// traveller's own return page routinely both arrive for the same payment.
/// </para>
/// </remarks>
public interface IOrderPaymentSettlement
{
    /// <summary>
    /// Asks the gateway what happened to one order payment, and acts on the answer.
    /// </summary>
    /// <param name="reference">Our own reference for the attempt, which the gateway quotes back.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// True when the gateway has no final answer yet, so whoever asked should ask again later.
    /// </returns>
    public Task<bool> SettleAsync(string reference, CancellationToken cancellationToken = default);
}
