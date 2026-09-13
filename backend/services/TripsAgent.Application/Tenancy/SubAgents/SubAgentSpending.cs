using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>
/// The allowance, from the booking path's point of view: reserve before the supplier is asked,
/// give it back if nothing was bought.
/// </summary>
/// <remarks>
/// <para>
/// <b>A principal is not capped.</b> Its own wallet is the limit, and it has no allowance row, so
/// every method here is a no-op for one. Only an agency whose root is somebody else is a sub-agent,
/// and that is read from the request's own claims rather than with a query.
/// </para>
/// <para>
/// <b>Reserve and release are paired to the wallet hold.</b> The allowance moves at exactly the
/// moments the hold does — reserved when the money is held, given back when the hold is released,
/// left alone when it is captured — so "spent this period" and "held or spent in the wallet" cannot
/// drift apart. A release carries the sub-agency and the amount, both of which every release site
/// already has on the hold in front of it.
/// </para>
/// </remarks>
public sealed class SubAgentSpending
{
    private readonly IAllowanceReservations _reservations;
    private readonly ITenantContext _tenant;

    public SubAgentSpending(IAllowanceReservations reservations, ITenantContext tenant)
    {
        _reservations = reservations;
        _tenant = tenant;
    }

    /// <summary>True when this request is being made by a sub-agent, and so is capped.</summary>
    public bool CallerIsSubAgent =>
        _tenant.AgencyId is { } agencyId
        && _tenant.RootAgencyId is { } rootId
        && agencyId != rootId;

    /// <summary>
    /// Counts <paramref name="amount"/> against the caller's allowance, or says why it cannot.
    /// </summary>
    /// <returns>
    /// <see cref="AllowanceReservation.Reserved"/> for a principal, which has no cap, and for a
    /// sub-agent within its cap.
    /// </returns>
    public Task<AllowanceReservation> ReserveAsync(
        string currency,
        Money amount,
        CancellationToken cancellationToken = default) =>
        CallerIsSubAgent
            ? _reservations.ReserveAsync(currency, amount.AmountMinor, cancellationToken)
            : Task.FromResult(AllowanceReservation.Reserved);

    /// <summary>
    /// Gives a reservation back. Does nothing for an agency that has no allowance, so every
    /// release site can call it without first asking whether the booking was a sub-agent's.
    /// </summary>
    public Task ReleaseAsync(
        Guid agencyId,
        string currency,
        Money amount,
        CancellationToken cancellationToken = default) =>
        amount <= Money.Zero
            ? Task.CompletedTask
            : _reservations.ReleaseAsync(agencyId, currency, amount.AmountMinor, cancellationToken);

    /// <summary>One sentence for the agent when a reservation was refused, or null when it was not.</summary>
    public static string? WhyRefused(AllowanceReservation outcome) => outcome switch
    {
        AllowanceReservation.Reserved => null,
        AllowanceReservation.NoAllowance =>
            "This agency has no spending allowance yet, so it cannot book. Ask the agency that "
            + "manages you to set one.",
        AllowanceReservation.Frozen =>
            "This agency's spending allowance is frozen. Ask the agency that manages you to lift it.",
        AllowanceReservation.Exceeded =>
            "This booking would take this agency past its spending allowance for the period. Ask "
            + "the agency that manages you to raise it, or wait for the allowance to start again.",
        _ => "This agency's spending allowance could not be checked, so nothing was booked.",
    };
}
