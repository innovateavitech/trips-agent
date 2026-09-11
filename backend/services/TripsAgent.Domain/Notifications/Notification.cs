using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Notifications;

/// <summary>
/// The values of <c>notifications.status</c>. Plain strings rather than an enum, for the same
/// reason as <c>OutboxMessageStatus</c>: they read the same in psql as in C#, and the dispatcher's
/// partial index can name them literally.
/// </summary>
public static class NotificationStatus
{
    /// <summary>Waiting to be sent, possibly after a failed attempt.</summary>
    public const string Queued = "queued";

    /// <summary>Handed to the provider, which accepted it. As far as M1 can tell, it is delivered.</summary>
    public const string Sent = "sent";

    /// <summary>
    /// The provider confirmed it reached the mailbox. Nothing sets this yet: SMTP tells us only
    /// that the relay accepted the message, and the ESP that would report more is not chosen
    /// (CLAUDE.md, "Stack"). The value exists so its adapter has somewhere to write.
    /// </summary>
    public const string Delivered = "delivered";

    /// <summary>Gave up after too many failed attempts, or could not be rendered at all. Needs a person.</summary>
    public const string Failed = "failed";

    /// <summary>The address rejected it permanently. Not retried — see <see cref="Notification.MarkBounced"/>.</summary>
    public const string Bounced = "bounced";
}

/// <summary>Who the notification is for, which is not the same as who it is about.</summary>
public enum NotificationRecipientType
{
    /// <summary>Somebody at the travel agency — an owner, a manager, a booking agent.</summary>
    AgencyUser = 1,

    /// <summary>The agency's own customer. Their mail carries the agency's brand, never ours.</summary>
    Traveller = 2,
}

/// <summary>
/// One message to one person, and the record of what happened to it.
/// </summary>
/// <remarks>
/// <para>
/// Queued inside the transaction that caused it — a KYB decision, a wallet credit — and sent
/// afterwards by <c>NotificationDispatcher</c> in the Worker. Same reasoning as the outbox
/// (ADR-0005): the business change and the intention to notify commit together, so there is no
/// moment where an agency is verified and nobody will ever be told.
/// </para>
/// <para>
/// <b>Payload, not prose.</b> The row holds the template key and the variables, not a rendered
/// body. Rendering happens at send time so the branding is whatever the agency's branding record
/// says <i>then</i>, and so a wording fix reaches everything still queued.
/// </para>
/// <para>
/// Tenant-scoped: an agency may read its own notification history — including the mail sent to its
/// travellers — and must never see another agency's. <see cref="AgencyId"/> carries the filter and
/// the row-level security policy. The dispatcher reads across agencies through
/// <c>IPlatformScope</c>, which is logged, rather than by dropping the filter.
/// </para>
/// </remarks>
public sealed class Notification : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>Longest error kept in <see cref="LastError"/>. The full exception is in the logs.</summary>
    public const int MaxErrorLength = 2000;

    private Notification()
    {
        TemplateKey = string.Empty;
        Locale = NotificationTemplate.DefaultLocale;
        RecipientAddress = string.Empty;
        RecipientName = string.Empty;
        Payload = "{}";
        DedupeKey = string.Empty;
        Status = NotificationStatus.Queued;
    }

    /// <summary>
    /// Queues a notification. Nothing is sent until the dispatcher picks the row up.
    /// </summary>
    /// <param name="agencyId">The agency this belongs to — the recipient's agency, or the traveller's agent.</param>
    /// <param name="templateKey">Which template renders it. Must exist in the catalog.</param>
    /// <param name="channel">How it is sent. Email only, for now.</param>
    /// <param name="locale">Which locale to render in.</param>
    /// <param name="recipientType">Agency staff or traveller. Decides whose brand wraps it.</param>
    /// <param name="recipientAddress">Where it goes — an email address for <see cref="NotificationChannel.Email"/>.</param>
    /// <param name="recipientName">Who it is addressed to, for the greeting.</param>
    /// <param name="payload">Template variables as JSON.</param>
    /// <param name="dedupeKey">
    /// What makes this notification the same as another. A unique index on it is what stops a job
    /// that runs three times from sending three emails.
    /// </param>
    /// <param name="queuedAt">Now, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="scheduledFor">
    /// The earliest it may be sent, for a reminder or a nudge. Defaults to <paramref name="queuedAt"/>.
    /// </param>
    /// <param name="recipientUserId">The user account, when the recipient has one. Travellers do not.</param>
    public static Notification Queue(
        Guid agencyId,
        string templateKey,
        NotificationChannel channel,
        string locale,
        NotificationRecipientType recipientType,
        string recipientAddress,
        string recipientName,
        string payload,
        string dedupeKey,
        DateTimeOffset queuedAt,
        DateTimeOffset? scheduledFor = null,
        Guid? recipientUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A notification belongs to an agency.", nameof(agencyId));
        }

        var due = scheduledFor ?? queuedAt;

        return new Notification
        {
            AgencyId = agencyId,
            TemplateKey = templateKey,
            Channel = channel,
            Locale = locale,
            RecipientType = recipientType,
            RecipientAddress = recipientAddress,
            RecipientName = recipientName,
            RecipientUserId = recipientUserId,
            Payload = payload,
            DedupeKey = dedupeKey,
            Status = NotificationStatus.Queued,
            ScheduledFor = due,
            NextAttemptAt = due,
        };
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Which template renders this. See <c>NotificationTemplateCatalog</c>.</summary>
    public string TemplateKey { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string Locale { get; private set; }

    public NotificationRecipientType RecipientType { get; private set; }

    /// <summary>The email address, for an email. Lower-cased by the queue before it gets here.</summary>
    public string RecipientAddress { get; private set; }

    public string RecipientName { get; private set; }

    /// <summary>The recipient's user account, when they have one. Null for a traveller.</summary>
    public Guid? RecipientUserId { get; private set; }

    /// <summary>Template variables as camelCase JSON. <c>jsonb</c>, so it can be read in psql.</summary>
    public string Payload { get; private set; }

    /// <summary>What makes this notification unique. Unique per agency; see <see cref="Queue"/>.</summary>
    public string DedupeKey { get; private set; }

    /// <summary>One of <see cref="NotificationStatus"/>.</summary>
    public string Status { get; private set; }

    /// <summary>Send attempts so far, successful or not.</summary>
    public int Attempts { get; private set; }

    /// <summary>The earliest this may be sent at all — a schedule, not a retry.</summary>
    public DateTimeOffset ScheduledFor { get; private set; }

    /// <summary>The earliest the dispatcher will try again. Moves out with each failure.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Which template version it was rendered from. Null until it renders.</summary>
    public int? TemplateVersion { get; private set; }

    /// <summary>When the provider accepted it.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>When the provider confirmed delivery, if it ever tells us.</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>
    /// The provider's own id for the message, so a bounce report weeks later can be matched back
    /// to the notification that caused it.
    /// </summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Why the most recent attempt failed, trimmed to <see cref="MaxErrorLength"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>The provider accepted it. It will not be sent again.</summary>
    public void MarkSent(int templateVersion, string? providerMessageId, DateTimeOffset sentAt)
    {
        Attempts++;
        Status = NotificationStatus.Sent;
        TemplateVersion = templateVersion;
        ProviderMessageId = providerMessageId;
        SentAt = sentAt;
        LastError = null;
    }

    /// <summary>The provider confirmed the mailbox received it.</summary>
    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        // Only from sent. A bounce is not upgraded by a late delivery report for a different
        // attempt, and a queued row cannot have been delivered by anybody.
        if (Status != NotificationStatus.Sent)
        {
            return;
        }

        Status = NotificationStatus.Delivered;
        DeliveredAt = deliveredAt;
    }

    /// <summary>
    /// The address rejected the message and will keep rejecting it.
    /// </summary>
    /// <remarks>
    /// Terminal on purpose: a mailbox that does not exist will not start existing because we tried
    /// four more times, and a relay that sees us retry into a hard bounce treats us as a spammer.
    /// The address is suppressed separately — see <c>SuppressedEmailAddress</c>.
    /// </remarks>
    public void MarkBounced(string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reason);

        Attempts++;
        Status = NotificationStatus.Bounced;
        LastError = Trim(reason);
        SentAt ??= at;
    }

    /// <summary>A send attempt failed.</summary>
    /// <param name="error">What went wrong. Trimmed to <see cref="MaxErrorLength"/>.</param>
    /// <param name="retryAt">When to try again, or null to give up and mark it failed.</param>
    public void RecordFailure(string error, DateTimeOffset? retryAt)
    {
        ArgumentNullException.ThrowIfNull(error);

        Attempts++;
        LastError = Trim(error);

        if (retryAt is { } next)
        {
            NextAttemptAt = next;
        }
        else
        {
            Status = NotificationStatus.Failed;
        }
    }

    private static string Trim(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
