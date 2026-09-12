namespace TripsAgent.Application.Commerce;

/// <summary>
/// The timings and limits of the traveller's buying flow (build plan F5).
/// </summary>
/// <remarks>
/// Bound from configuration under <c>Commerce</c>, so a cart's life can be tuned without a
/// deployment of new code. The defaults are the ones the plan describes.
/// </remarks>
public sealed class CommerceOptions
{
    public const string SectionName = "Commerce";

    /// <summary>
    /// How long an untouched cart lives. Every change to it starts the clock again.
    /// </summary>
    /// <remarks>
    /// Long enough to think about a trip over lunch, short enough that a cart abandoned on a phone
    /// does not sit on seats nobody can buy for a week. Seats themselves are held for far less —
    /// only from the moment checkout starts (<c>DepartureSeats.DefaultHoldTimeToLive</c>).
    /// </remarks>
    public TimeSpan CartLifetime { get; set; } = TimeSpan.FromDays(2);

    /// <summary>
    /// How long a traveller has to finish paying before the fares and seats held for them go back.
    /// </summary>
    /// <remarks>
    /// A supplier's own ticket time limit is usually shorter, and always wins: the deadline is the
    /// soonest of the two (decision 10 — the checkout shows a live countdown).
    /// </remarks>
    public TimeSpan PaymentWindow { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>The most lines one cart may hold. A guard on an anonymous endpoint, not a product rule.</summary>
    public int MaxCartItems { get; set; } = 20;

    /// <summary>The most travellers one line may be for.</summary>
    public int MaxPaxPerItem { get; set; } = 9;

    /// <summary>How long a traveller's "manage my booking" link works for.</summary>
    public TimeSpan BookingLinkLifetime { get; set; } = TimeSpan.FromDays(90);
}
