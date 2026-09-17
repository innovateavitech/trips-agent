namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>What happened when a sub-agent tried to reserve against its allowance.</summary>
public enum AllowanceReservation
{
    /// <summary>Reserved. The amount is counted against this period's cap.</summary>
    Reserved = 1,

    /// <summary>This agency has no allowance in this currency, so it may not spend at all.</summary>
    NoAllowance = 2,

    /// <summary>The allowance is frozen — the sub-agent is frozen, or the principal froze it.</summary>
    Frozen = 3,

    /// <summary>The cap would be passed. Nothing was reserved.</summary>
    Exceeded = 4,

    /// <summary>No agency is resolved for this request, so there is nobody to reserve for.</summary>
    NoTenant = 5,
}

/// <summary>
/// The two statements that move a sub-agent's allowance, run as single conditional updates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a port and not EF.</b> Loading the allowance, checking the cap in C# and saving
/// it back is the obvious implementation and it is wrong: two bookings that read the same
/// <c>spent_minor</c> both pass the check, and the second overwrites the first. Optimistic
/// concurrency turns that into a retry storm at exactly the moment an agency is busiest.
/// </para>
/// <para>
/// One <c>UPDATE … WHERE spent_minor + @amount &lt;= limit_minor</c> has no such window: PostgreSQL
/// serialises concurrent updates of a row and re-evaluates the condition against the committed
/// version, so the second booking simply updates no rows and is refused. The implementation lives
/// in Infrastructure because the guarantee is the database's, not ours.
/// </para>
/// <para>
/// It is also the one write a sub-agent's request makes to a row its principal owns, so it goes
/// through a SECURITY DEFINER function that can do nothing else — see the
/// <c>AddSubAgentNetwork</c> migration.
/// </para>
/// </remarks>
public interface IAllowanceReservations
{
    /// <summary>
    /// Counts <paramref name="amountMinor"/> against the calling agency's own allowance.
    /// </summary>
    /// <remarks>
    /// The agency is taken from the database session rather than a parameter, so a request can
    /// only ever spend its own allowance however this is called.
    /// </remarks>
    public Task<AllowanceReservation> ReserveAsync(
        string currency,
        long amountMinor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a reservation back after a booking that failed, was reversed or lapsed.
    /// </summary>
    /// <remarks>
    /// Takes the sub-agency because the caller is often a background job rather than the sub-agent
    /// — the checkout sweeper, the ticket time limit monitor. Only that sub-agent, its principal
    /// or a platform scope may call it; anything else is refused and returns false. So is a release
    /// of more than the allowance holds (issue 175): the database gives back at most what was
    /// reserved, and refuses the rest rather than quietly clamping it at nothing spent.
    /// </remarks>
    public Task<bool> ReleaseAsync(
        Guid subAgencyId,
        string currency,
        long amountMinor,
        CancellationToken cancellationToken = default);
}
