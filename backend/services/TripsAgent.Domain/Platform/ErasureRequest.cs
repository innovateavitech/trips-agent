using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Platform;

/// <summary>Where an erasure request got to.</summary>
public enum ErasureRequestStatus
{
    /// <summary>Recorded, and the anonymisation has not finished.</summary>
    Requested = 1,

    /// <summary>The person's details have been replaced everywhere this system holds them.</summary>
    Completed = 2,

    /// <summary>Refused, with the reason on the row: an order still in flight, or an open dispute.</summary>
    Refused = 3,
}

/// <summary>
/// A request to erase one person's details, and what it did (issue 106).
/// </summary>
/// <remarks>
/// <para>
/// <b>The record is the point.</b> NDPA 2023 gives a person the right to be erased, and gives us the
/// duty to show that we did it. So this row survives the erasure: who asked, when, which customer,
/// on what stated reason, and how many rows in which tables were changed. It holds no name, no email
/// and no phone number — the request outlives the details, so keeping them here would be a copy of
/// exactly what was erased.
/// </para>
/// <para>
/// <b>Erasure here means anonymisation.</b> Ledger entries, order lines, invoices and audit rows stay
/// exactly as they are, and stop identifying anyone: see
/// <c>docs/adr/0009-ndpa-erasure-as-anonymisation.md</c>. It cannot be undone and there is no copy of
/// what was erased anywhere in this system.
/// </para>
/// </remarks>
public sealed class ErasureRequest : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    /// <summary>A reason shorter than this is not a reason.</summary>
    public const int MinimumReasonLength = 10;

    /// <summary>Longer than this is a case file, and belongs in the ticket rather than here.</summary>
    public const int MaximumReasonLength = 1_000;

    private ErasureRequest()
    {
        Reason = string.Empty;
    }

    /// <summary>Records the request, before anything is changed.</summary>
    /// <param name="agencyId">The agency whose customer it is.</param>
    /// <param name="customerId">The customer being erased. Nothing identifying is stored beside it.</param>
    /// <param name="reason">Why: the person's request, a regulator's direction, a court order.</param>
    /// <param name="requestedByUserId">The Trips staff member acting on it.</param>
    /// <param name="at">When it was recorded.</param>
    public static ErasureRequest Record(Guid agencyId, Guid customerId, string reason, Guid? requestedByUserId, DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);

        var stated = (reason ?? string.Empty).Trim();

        if (stated.Length < MinimumReasonLength)
        {
            throw new ArgumentException(
                $"An erasure request needs a stated reason of at least {MinimumReasonLength} characters. "
                + "It is the only record of why somebody's details were destroyed.",
                nameof(reason));
        }

        return new ErasureRequest
        {
            AgencyId = agencyId,
            CustomerId = customerId,
            Reason = stated[..Math.Min(stated.Length, MaximumReasonLength)],
            RequestedByUserId = requestedByUserId,
            RequestedAt = at.ToUniversalTime(),
            Status = ErasureRequestStatus.Requested,
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>The customer row that was anonymised. It still exists; it no longer names anyone.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>Why the erasure was carried out, in the words of whoever carried it out.</summary>
    public string Reason { get; private set; }

    /// <summary>The Trips staff member who ran it. Null only for a request made by the platform itself.</summary>
    public Guid? RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public ErasureRequestStatus Status { get; private set; }

    /// <summary>What was changed, as JSON: a count per table. Never the values themselves.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Why it was refused, when it was.</summary>
    public string? RefusalReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The person's details are gone. <paramref name="outcome"/> is counts, never values.</summary>
    public void Complete(string outcome, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        Status = ErasureRequestStatus.Completed;
        Outcome = outcome;
        CompletedAt = at.ToUniversalTime();
    }

    /// <summary>
    /// Nothing was changed, and this says why.
    /// </summary>
    /// <remarks>
    /// A refusal is recorded rather than thrown away: "we asked and were told no, because the booking
    /// had not happened yet" is exactly what a regulator asks about later.
    /// </remarks>
    public void Refuse(string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = ErasureRequestStatus.Refused;
        RefusalReason = reason[..Math.Min(reason.Length, MaximumReasonLength)];
        CompletedAt = at.ToUniversalTime();
    }
}
