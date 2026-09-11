using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Notifications;

namespace TripsAgent.Application.Notifications;

/// <summary>
/// Tells the Worker a notification is waiting. Published through the outbox, consumed from
/// <c>notifications.email</c>.
/// </summary>
/// <remarks>
/// Carries only the id. The row is the truth — what to send, to whom, and whether it already went —
/// so a redelivered message finds the row finished and does nothing.
/// </remarks>
public sealed record NotificationQueued(Guid NotificationId);

/// <summary>One email to queue.</summary>
/// <param name="AgencyId">The agency it belongs to: the recipient's own, or the traveller's agent.</param>
/// <param name="TemplateKey">A key from <see cref="NotificationTemplateCatalog"/>.</param>
/// <param name="RecipientAddress">Where it goes.</param>
/// <param name="RecipientName">Who it is addressed to, for the greeting.</param>
/// <param name="Values">The template's variables. Every one it declares must be here.</param>
/// <param name="DedupeKey">
/// What makes this notification the same as another — usually the template key and the id of the
/// thing it is about, e.g. <c>kyb.approved:{submissionId}</c>. Queueing the same key twice for one
/// agency queues it once.
/// </param>
/// <param name="RecipientUserId">The recipient's user account, when they have one.</param>
public sealed record EmailNotificationRequest(
    Guid AgencyId,
    string TemplateKey,
    string RecipientAddress,
    string RecipientName,
    IReadOnlyDictionary<string, string> Values,
    string DedupeKey,
    Guid? RecipientUserId = null);

/// <summary>
/// Queues notifications inside the caller's own unit of work.
/// </summary>
/// <remarks>
/// Nothing is sent and nothing is saved here. The notification row and its outbox message are
/// added to the caller's context, so they commit with the caller's save — or not at all, if the
/// save fails. That is the whole guarantee: an agency is never verified without being told, and
/// never told about a decision that rolled back.
/// </remarks>
public interface INotifier
{
    /// <summary>Stages an email for sending once the caller saves.</summary>
    /// <returns>False when a notification with the same dedupe key already exists — nothing was added.</returns>
    /// <exception cref="ArgumentException">
    /// The template does not exist, or a variable it needs is missing. A bug in the caller, and
    /// better found here than by the dispatcher after the caller's transaction has committed.
    /// </exception>
    public Task<bool> QueueEmailAsync(EmailNotificationRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class Notifier : INotifier
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IAppDbContext _db;
    private readonly IOutbox _outbox;

    public Notifier(IAppDbContext db, IOutbox outbox)
    {
        _db = db;
        _outbox = outbox;
    }

    public async Task<bool> QueueEmailAsync(EmailNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = NotificationTemplateCatalog.Find(request.TemplateKey, NotificationChannel.Email)
            ?? throw new ArgumentException(
                $"There is no email template '{request.TemplateKey}'. See NotificationTemplateCatalog.",
                nameof(request));

        var missing = template.Tokens.Where(token => !request.Values.ContainsKey(token)).ToList();

        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Template '{request.TemplateKey}' needs {string.Join(", ", missing)}.", nameof(request));
        }

        // Checked in memory first: the same unit of work may already have staged it, and that row
        // is not in the database yet for the query below to find.
        if (_db.Notifications.Local.Any(n => Matches(n, request))
            || await _db.Notifications.AnyAsync(
                n => n.AgencyId == request.AgencyId && n.DedupeKey == request.DedupeKey,
                cancellationToken))
        {
            return false;
        }

        var notification = Notification.Queue(
            request.AgencyId,
            template.Key,
            NotificationChannel.Email,
            template.Locale,
            template.Audience == NotificationAudience.Traveller
                ? NotificationRecipientType.Traveller
                : NotificationRecipientType.AgencyUser,
            request.RecipientAddress.Trim().ToLowerInvariant(),
            request.RecipientName,
            JsonSerializer.Serialize(request.Values, PayloadJson),
            request.DedupeKey,
            request.RecipientUserId);

        _db.Notifications.Add(notification);
        _outbox.Enqueue(new NotificationQueued(notification.Id), request.AgencyId);

        return true;
    }

    /// <summary>Reads a stored payload back into the variables it was queued with.</summary>
    public static IReadOnlyDictionary<string, string> ReadPayload(string payload) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(payload, PayloadJson)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool Matches(Notification notification, EmailNotificationRequest request) =>
        notification.AgencyId == request.AgencyId && notification.DedupeKey == request.DedupeKey;
}
