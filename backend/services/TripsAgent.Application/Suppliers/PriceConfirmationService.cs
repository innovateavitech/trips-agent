using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// Confirms a booking's price with its supplier, lets the booking judge every hash, and raises a
/// security alert when one fails (#35).
/// </summary>
/// <remarks>
/// <para>
/// A hash that does not verify means the price that reached us is not the price the supplier signed:
/// an edit in transit, a misbehaving proxy, or someone probing. The checkout is already stopped by
/// then — <see cref="SupplierBooking.RecordPriceConfirmation"/> marks the booking PriceRejected when
/// any single element fails, and a rejected booking cannot move on to issue. This makes sure a person
/// hears about it, at P1, because the next thing to go wrong may be real money.
/// </para>
/// <para>
/// It does not save. The caller owns the unit of work: the checkout saga (#42) records the booking
/// together with whatever else its step changed, in one transaction.
/// </para>
/// </remarks>
public sealed class PriceConfirmationService
{
    private readonly ISupplierAdapterRegistry _adapters;
    private readonly IPlatformAlerter _alerter;
    private readonly TimeProvider _clock;

    public PriceConfirmationService(ISupplierAdapterRegistry adapters, IPlatformAlerter alerter, TimeProvider clock)
    {
        _adapters = adapters;
        _alerter = alerter;
        _clock = clock;
    }

    /// <returns>The confirmation as the supplier sent it. The verdict is on the booking.</returns>
    public async Task<SupplierPriceConfirmation> ConfirmAsync(
        SupplierBooking booking,
        string supplierCode,
        SupplierPriceConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierCode);
        ArgumentNullException.ThrowIfNull(request);

        var adapter = _adapters.Resolve(supplierCode, booking.ProductType);
        var confirmation = await adapter.ConfirmPriceAsync(
            new SupplierCallContext(booking.AgencyId, booking.Id), request, cancellationToken);

        booking.RecordPriceConfirmation(confirmation.Lines, _clock.GetUtcNow());

        if (booking.Status == SupplierBookingStatus.PriceRejected)
        {
            await RaiseIntegrityAlertAsync(booking.AgencyId, booking.Id, supplierCode, confirmation.Lines, cancellationToken);
        }

        return confirmation;
    }

    /// <summary>
    /// Raises the P1 for a confirmation whose hashes did not all verify. Also used by the checkout, which
    /// checks a confirmation before any booking exists for it.
    /// </summary>
    public Task RaiseIntegrityAlertAsync(
        Guid agencyId,
        Guid? supplierBookingId,
        string supplierCode,
        IReadOnlyList<PriceConfirmationLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Which elements, by position and confirmation code — never the hashes or anything keyed by the
        // merchant secret. The supplier_api_calls row holds exactly what arrived.
        var failed = lines
            .Select((line, index) => (line, index))
            .Where(pair => !SupplierBookingConfirmation.HashesMatch(pair.line.HashExpected, pair.line.HashReceived))
            .Select(pair => $"element {pair.index + 1} ({pair.line.ConfirmationCode})")
            .ToList();

        var subject = supplierBookingId is { } id ? $"Booking {id}" : "A price confirmation";

        return _alerter.RaiseAsync(
            new PlatformAlert(
                AlertSeverity.P1,
                "A supplier price confirmation failed its integrity check",
                $"{subject} with {supplierCode}: {string.Join(", ", failed)} of {lines.Count} "
                + "did not match the hash we computed with the merchant key, so the price that arrived is not the "
                + "price the supplier signed. The booking is blocked from issuing and nothing was charged for it. "
                + "Read the supplier_api_calls rows for this booking to see exactly what arrived, and treat it as a "
                + "possible tamper until shown otherwise.",
                Source: nameof(PriceConfirmationService),
                AgencyId: agencyId),
            cancellationToken);
    }
}
