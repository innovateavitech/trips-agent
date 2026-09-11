using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Notifications;

/// <summary>
/// An address we have stopped sending to, because it bounced permanently.
/// </summary>
/// <remarks>
/// <para>
/// Suppression is the half of bounce handling that protects future mail. A relay judges a sender
/// partly on how much of their mail hard-bounces, so continuing to send to a mailbox that does not
/// exist slowly poisons delivery for every agency on the platform — including the agencies whose
/// addresses are fine.
/// </para>
/// <para>
/// Platform-wide, not per agency: a mailbox that has been deleted is deleted for everyone. So
/// there is no <c>agency_id</c> and no tenant filter, and the row holds nothing about an agency's
/// business — only an address, a reason and a date. It is read by the dispatcher, which already
/// runs across agencies.
/// </para>
/// <para>
/// Lifting a suppression is deliberate and manual (<see cref="Lift"/>): the usual cause is the
/// customer fixing their mailbox and telling support, and nothing automatic can know that.
/// </para>
/// </remarks>
public sealed class SuppressedEmailAddress : Entity, IAuditableEntity
{
    private SuppressedEmailAddress()
    {
        Address = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Suppresses <paramref name="address"/> after a permanent rejection.</summary>
    /// <param name="address">The mailbox that rejected us. Compared case-insensitively by the column type.</param>
    /// <param name="reason">What the provider said, so support can explain it.</param>
    /// <param name="at">When it happened.</param>
    /// <param name="notificationId">The notification that discovered it, for tracing.</param>
    public static SuppressedEmailAddress Create(
        string address,
        string reason,
        DateTimeOffset at,
        Guid? notificationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(reason);

        return new SuppressedEmailAddress
        {
            Address = address.Trim().ToLowerInvariant(),
            Reason = reason.Length <= 500 ? reason : reason[..500],
            SuppressedAt = at,
            DiscoveredByNotificationId = notificationId,
        };
    }

    /// <summary>The address, lower-cased. Unique.</summary>
    public string Address { get; private set; }

    /// <summary>What the provider said when it rejected us.</summary>
    public string Reason { get; private set; }

    public DateTimeOffset SuppressedAt { get; private set; }

    /// <summary>Which notification hit the bounce.</summary>
    public Guid? DiscoveredByNotificationId { get; private set; }

    /// <summary>Set when somebody decided the address works again. Null while it is suppressed.</summary>
    public DateTimeOffset? LiftedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while mail to this address is being held back.</summary>
    public bool IsActive => LiftedAt is null;

    /// <summary>Allows mail to this address again. Kept as a row, so the history survives.</summary>
    public void Lift(DateTimeOffset at) => LiftedAt ??= at;
}
