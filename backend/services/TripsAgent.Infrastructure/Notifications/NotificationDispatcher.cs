using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>A notification names a file to attach that is not there to attach. Retrying will not help.</summary>
internal sealed class NotificationAttachmentMissingException : Exception
{
    public NotificationAttachmentMissingException(string message)
        : base(message)
    {
    }

    public NotificationAttachmentMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NotificationAttachmentMissingException()
        : base("An attachment is missing.")
    {
    }
}

/// <summary>What one attempt to send a notification came to.</summary>
public enum NotificationDispatchOutcome
{
    /// <summary>The provider accepted it.</summary>
    Sent = 1,

    /// <summary>
    /// Nothing to do: already finished, unknown, another Worker holds it right now, or an earlier
    /// attempt's outcome is unknown and it is waiting for a person.
    /// </summary>
    Skipped = 2,

    /// <summary>The address rejected it permanently, now or before. Not retried.</summary>
    Bounced = 3,

    /// <summary>Failed this time and should be tried again.</summary>
    RetryLater = 4,

    /// <summary>Failed for the last time, or could never have worked. Marked failed for a person.</summary>
    GaveUp = 5,
}

/// <summary>
/// Renders and sends one queued notification, and records what happened. Called by
/// <see cref="NotificationQueuedConsumer"/> for every <c>NotificationQueued</c> message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three steps, and the email is sent in none of the database ones.</b>
/// </para>
/// <list type="number">
/// <item><b>Claim</b>, in one short transaction: lock the row with <c>FOR UPDATE SKIP LOCKED</c>
/// while it is still <c>queued</c>, render it, and commit it as <c>sending</c> with the attempt
/// counted. From here on nothing else — a redelivered message, a second Worker — can claim it.</item>
/// <item><b>Send</b>, outside any transaction and outside EF's retry.</item>
/// <item><b>Record</b> the result in a second save.</item>
/// </list>
/// <para>
/// Why it matters: the production context retries on transient database errors by <i>replaying</i>
/// the work it was given. If the send were inside that work, a database that accepted reads but
/// refused writes would replay the send four times per delivery, and the broker would redeliver
/// four more times — about twenty copies of one email. Now a replay can only repeat database work.
/// </para>
/// <para>
/// <b>The price is an unknown outcome instead of a duplicate.</b> If the Worker dies during the send,
/// or the database refuses the record, the row stays <c>sending</c> and is never resent
/// automatically — the relay may already have delivered it (the same rule as ADR-0003: an unknown
/// outcome is not a failure). It is logged, and a person decides. The one exception is shutdown:
/// a send cancelled by the Worker stopping goes back to <c>queued</c>, because a deploy should not
/// strand mail. That can repeat one email the relay had accepted just as it was cancelled, and the
/// attempt still counts, so it can never loop.
/// </para>
/// <para>
/// <b>Retries belong to the broker.</b> A transient send failure puts the row back to <c>queued</c>
/// and reports <see cref="NotificationDispatchOutcome.RetryLater"/>; the consumer throws, and
/// MassTransit's retry policy for <c>notifications.email</c> redelivers with backoff. The fifth
/// attempt marks the row failed and the broker moves the message to <c>notifications.email_error</c>.
/// </para>
/// <para>
/// Runs inside <see cref="IPlatformScope"/>: the Worker has no tenant of its own, and each message
/// may belong to any agency. The scope is logged, which dropping the filter would not be.
/// </para>
/// </remarks>
public sealed partial class NotificationDispatcher(
    AppDbContext dbContext,
    IEmailSender emailSender,
    IPlatformScope platformScope,
    IAgencyLogoSource logos,
    IBlobStorage storage,
    TimeProvider clock,
    ILogger<NotificationDispatcher> logger)
{
    /// <summary>Attempts before a notification is marked failed and dead-lettered. Issue #45.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Sends <paramref name="notificationId"/> if it is still waiting to be sent.</summary>
    /// <exception cref="Exception">
    /// The result could not be recorded. The row stays <c>sending</c>; the broker's retry will find
    /// it claimed and leave it alone.
    /// </exception>
    public async Task<NotificationDispatchOutcome> DispatchAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        using var scope = platformScope.Enter("notification dispatch — the Worker sends every agency's notifications");

        // Step 1. AddInfrastructure turns on EF's retry-on-transient-failure, which refuses a
        // hand-opened transaction unless the whole unit is handed to it to replay. Safe here: the
        // claim only reads and writes the database, so replaying it sends nothing.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var claim = await strategy.ExecuteAsync(
            notificationId,
            (_, id, token) => ClaimAsync(id, token),
            verifySucceeded: null,
            cancellationToken);

        if (claim.Notification is null)
        {
            await ExplainSkipAsync(notificationId, claim.Outcome, cancellationToken);
            return claim.Outcome;
        }

        if (claim.Message is null)
        {
            // Finished without sending — suppressed, or nothing that could be rendered. Already saved.
            return claim.Outcome;
        }

        // Steps 2 and 3.
        return await SendAndRecordAsync(claim.Notification, claim.Message, claim.TemplateVersion, cancellationToken);
    }

    private async Task<Claim> ClaimAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        // A replay starts clean, not with the last attempt's half-made changes.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 'queued' as a literal, like the outbox: a row that is sending or finished is not claimable.
        var notification = await dbContext.Notifications
            .FromSql($"""
                SELECT *
                FROM notifications.notifications
                WHERE id = {notificationId}
                  AND status = 'queued'
                FOR UPDATE SKIP LOCKED
                """)
            .FirstOrDefaultAsync(cancellationToken);

        if (notification is null)
        {
            return Claim.Nothing;
        }

        var claim = await PrepareAsync(notification, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return claim;
    }

    /// <summary>
    /// Everything that can be decided before the provider is called: whether it can be sent at all,
    /// and what exactly to send. Either finishes the notification or claims it as sending.
    /// </summary>
    private async Task<Claim> PrepareAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (notification.Channel != NotificationChannel.Email)
        {
            notification.RecordFailure($"No provider for the {notification.Channel} channel yet.", giveUp: true);
            return Claim.Finished(notification, NotificationDispatchOutcome.GaveUp);
        }

        if (await IsSuppressedAsync(notification.RecipientAddress, cancellationToken))
        {
            notification.MarkBounced("The address is on the suppression list after an earlier permanent bounce.", clock.GetUtcNow());
            LogSuppressed(logger, notification.Id);
            return Claim.Finished(notification, NotificationDispatchOutcome.Bounced);
        }

        var template = await NotificationTemplateSeeder.FindActiveAsync(
            dbContext, notification.TemplateKey, notification.Channel, notification.Locale, cancellationToken);

        if (template is null)
        {
            // Not retried: five more attempts will not seed a template. Running `migrate` will.
            return GiveUpBeforeSending(
                notification,
                $"No active template '{notification.TemplateKey}' for {notification.Channel}/{notification.Locale}. "
                + "Templates are seeded by the migrate command — has it run since this build was deployed?");
        }

        EmailMessage message;
        try
        {
            var (brand, logo) = await BrandForAsync(notification, template.Audience, cancellationToken);
            var rendered = NotificationRenderer.Render(
                template, brand, notification.RecipientName, Notifier.ReadPayload(notification.Payload));

            message = new EmailMessage(
                notification.RecipientAddress,
                rendered.Subject,
                rendered.Html,
                rendered.Text,

                // A traveller sees the agency's name as the sender, and a reply reaches the agency.
                FromName: template.Audience == NotificationAudience.Traveller ? brand.Name : null,
                ReplyTo: brand.ReplyTo,

                // The logo travels inside the message, which the header refers to as cid:agency-logo:
                // a linked image would be fetched from a server whose name gives away who sent it.
                Attachments: logo is null
                    ? null
                    : [new EmailAttachment("logo.png", "image/png", logo.Png, NotificationRenderer.InlineLogoContentId)]);
        }
        catch (NotificationRenderException ex)
        {
            return GiveUpBeforeSending(notification, ex.Message);
        }

        notification.MarkSending();
        return new Claim(NotificationDispatchOutcome.Sent, notification, message, template.Version);
    }

    private async Task<NotificationDispatchOutcome> SendAndRecordAsync(
        Notification notification,
        EmailMessage message,
        int templateVersion,
        CancellationToken cancellationToken)
    {
        var suppress = false;
        NotificationDispatchOutcome outcome;

        try
        {
            // Read here rather than in the claim, so the claim's row lock is never held while files
            // are fetched from storage. A failure here is before the send: nothing has gone out.
            var outgoing = await WithAttachmentsAsync(notification, message, cancellationToken);

            var receipt = await emailSender.SendAsync(outgoing, cancellationToken);
            notification.MarkSent(templateVersion, receipt.ProviderMessageId, clock.GetUtcNow());
            outcome = NotificationDispatchOutcome.Sent;
        }
        catch (NotificationAttachmentMissingException ex)
        {
            // The file is not there to attach, and retrying will not put it there.
            notification.RecordFailure(ex.Message, giveUp: true);
            LogCannotRender(logger, notification.Id, notification.TemplateKey, ex.Message);
            outcome = NotificationDispatchOutcome.GaveUp;
        }
        catch (EmailRejectedException ex) when (ex.AddressIsUndeliverable)
        {
            notification.MarkBounced(ex.Message, clock.GetUtcNow());
            suppress = true;
            LogBounced(logger, ex, notification.Id);
            outcome = NotificationDispatchOutcome.Bounced;
        }
        catch (EmailRejectedException ex)
        {
            // Refused for good, but not because the address is bad — relaying denied, our IP on a
            // blocklist. Retrying will not help; suppressing would punish a working inbox for our
            // own configuration. Failed, for a person to look at.
            notification.RecordFailure(ex.Message, giveUp: true);
            LogRefused(logger, ex, notification.Id, notification.TemplateKey);
            outcome = NotificationDispatchOutcome.GaveUp;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // The Worker is stopping. Back in the queue for the redelivery rather than stranded as
            // sending — see the remarks for the one duplicate this can cause. Then let it escape.
            notification.RecordFailure($"Interrupted by shutdown while sending: {ex.GetType().Name}", giveUp: false);
            await RecordAsync(notification, suppress: false);
            throw;
        }
        catch (Exception ex)
        {
            // The attempt was counted when it was claimed.
            var error = $"{ex.GetType().Name}: {ex.Message}";

            if (notification.Attempts >= MaxAttempts)
            {
                notification.RecordFailure(error, giveUp: true);
                LogGaveUp(logger, ex, notification.Id, notification.TemplateKey, MaxAttempts);
                outcome = NotificationDispatchOutcome.GaveUp;
            }
            else
            {
                notification.RecordFailure(error, giveUp: false);
                LogWillRetry(logger, ex, notification.Id, notification.Attempts);
                outcome = NotificationDispatchOutcome.RetryLater;
            }
        }

        await RecordAsync(notification, suppress);
        return outcome;
    }

    /// <summary>
    /// Step 3. A plain save, which EF's strategy may replay on its own — safe, because it is only an
    /// update to one row and perhaps one insert, and nothing is sent inside it.
    /// </summary>
    /// <remarks>
    /// Not cancellable: whatever the provider did has happened, and a Worker that is stopping should
    /// still write it down. The save is one short statement.
    /// </remarks>
    private async Task RecordAsync(Notification notification, bool suppress)
    {
        try
        {
            if (suppress && !await IsSuppressedAsync(notification.RecipientAddress, CancellationToken.None))
            {
                dbContext.SuppressedEmailAddresses.Add(SuppressedEmailAddress.Create(
                    notification.RecipientAddress, notification.LastError ?? "Permanent bounce.", clock.GetUtcNow(), notification.Id));
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Thrown on, so the broker retries the message — and that retry finds the row still
            // 'sending' and leaves it alone, which is the point.
            LogOutcomeNotRecorded(logger, ex, notification.Id, notification.Status);
            throw;
        }
    }

    /// <summary>
    /// Nothing was claimed. Usually that is a redelivery of something finished; say so when it is
    /// the one case a person needs to know about.
    /// </summary>
    private async Task ExplainSkipAsync(Guid notificationId, NotificationDispatchOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome != NotificationDispatchOutcome.Skipped)
        {
            return;
        }

        var stuck = await dbContext.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.Id == notificationId && n.Status == NotificationStatus.Sending, cancellationToken);

        if (stuck)
        {
            LogOutcomeUnknown(logger, notificationId);
        }
    }

    private Claim GiveUpBeforeSending(Notification notification, string reason)
    {
        notification.RecordFailure(reason, giveUp: true);
        LogCannotRender(logger, notification.Id, notification.TemplateKey, reason);
        return Claim.Finished(notification, NotificationDispatchOutcome.GaveUp);
    }

    /// <summary>
    /// The notification's stored attachments — an invoice, a voucher — added to the message.
    /// </summary>
    /// <remarks>
    /// Only the notification's own agency's files, and only servable ones. The dispatcher reads
    /// across agencies, so the agency is named in the query rather than left to a filter that the
    /// platform scope has lifted.
    /// </remarks>
    /// <exception cref="NotificationAttachmentMissingException">An attachment is not such a file.</exception>
    private async Task<EmailMessage> WithAttachmentsAsync(
        Notification notification,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        if (notification.AttachmentAssetIds.Count == 0)
        {
            return message;
        }

        var ids = notification.AttachmentAssetIds.ToList();

        var assets = await dbContext.Assets
            .AsNoTracking()
            .Where(asset => ids.Contains(asset.Id) && asset.AgencyId == notification.AgencyId)
            .ToListAsync(cancellationToken);

        var attachments = new List<EmailAttachment>(message.Attachments ?? []);

        foreach (var id in ids)
        {
            var asset = assets.FirstOrDefault(a => a.Id == id);

            if (asset is not { IsServable: true, ContentType: { } contentType })
            {
                throw new NotificationAttachmentMissingException(
                    $"Attachment {id} is not a servable file belonging to agency {notification.AgencyId}.");
            }

            await using var content = await storage.OpenReadAsync(asset.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);

            attachments.Add(new EmailAttachment(asset.FileName, contentType, buffer.ToArray()));
        }

        return message with { Attachments = attachments };
    }

    /// <summary>
    /// Whose brand wraps the message — ours for agency staff, the agency's own, always, for a
    /// traveller — and the agency's logo to send inside it, when it has one.
    /// </summary>
    private async Task<(NotificationBrand Brand, AgencyLogo? Logo)> BrandForAsync(
        Notification notification,
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        if (audience == NotificationAudience.AgencyStaff)
        {
            return (NotificationBrand.Platform, null);
        }

        var agency = await dbContext.Agencies
            .AsNoTracking()
            .FirstAsync(a => a.Id == notification.AgencyId, cancellationToken);

        var branding = await dbContext.AgencyBranding
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.AgencyId == notification.AgencyId, cancellationToken);

        // The agency has no inbox of its own on record yet, so a traveller's reply goes to whoever
        // registered it — the owner. Agency contact details arrive with the branding screens.
        var owner = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.AgencyId == notification.AgencyId)
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        // Null when there is no servable logo, or it cannot be read: the header shows the name instead.
        var logo = await logos.LoadAsync(notification.AgencyId, cancellationToken);

        var brand = new NotificationBrand(
            agency.TradingName ?? agency.LegalName,
            branding?.PrimaryColor ?? AgencyBranding.DefaultPrimaryColor,
            LogoUrl: logo is null ? null : NotificationRenderer.InlineLogoUrl,
            Contact: branding?.ContactAddress,
            ReplyTo: owner);

        return (brand, logo);
    }

    private Task<bool> IsSuppressedAsync(string address, CancellationToken cancellationToken) =>
        dbContext.SuppressedEmailAddresses.AnyAsync(
            s => s.Address == address && s.LiftedAt == null,
            cancellationToken);

    /// <summary>What the claim step decided.</summary>
    /// <param name="Outcome">The outcome, when the claim step finished it; <c>Sent</c> as a placeholder when claimed.</param>
    /// <param name="Notification">The row, or null when there was nothing to claim.</param>
    /// <param name="Message">What to send, or null when there is nothing to send.</param>
    /// <param name="TemplateVersion">Which template version rendered <paramref name="Message"/>.</param>
    private sealed record Claim(
        NotificationDispatchOutcome Outcome,
        Notification? Notification,
        EmailMessage? Message,
        int TemplateVersion)
    {
        public static Claim Nothing { get; } = new(NotificationDispatchOutcome.Skipped, null, null, 0);

        public static Claim Finished(Notification notification, NotificationDispatchOutcome outcome) =>
            new(outcome, notification, null, 0);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Notification {NotificationId} failed on attempt {Attempt}; the broker will retry it.")]
    private static partial void LogWillRetry(ILogger logger, Exception exception, Guid notificationId, int attempt);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Notification {NotificationId} ({TemplateKey}) failed {Attempts} times and has been marked failed. "
                  + "It will not be retried automatically; a person needs to look at it.")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, Guid notificationId, string templateKey, int attempts);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Notification {NotificationId} ({TemplateKey}) cannot be sent and has been marked failed: {Reason}")]
    private static partial void LogCannotRender(ILogger logger, Guid notificationId, string templateKey, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Notification {NotificationId} ({TemplateKey}) was refused by the relay for a reason that is not the "
                  + "address — check the relay credentials and our sending reputation. Marked failed; the address "
                  + "is not suppressed.")]
    private static partial void LogRefused(ILogger logger, Exception exception, Guid notificationId, string templateKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Notification {NotificationId} bounced permanently; the address is now suppressed.")]
    private static partial void LogBounced(ILogger logger, Exception exception, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Notification {NotificationId} was not sent: its address is suppressed after an earlier bounce.")]
    private static partial void LogSuppressed(ILogger logger, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Notification {NotificationId} reached '{Status}' but the result could not be saved. The row is left "
                  + "'sending' and will NOT be sent again automatically: the relay may have delivered it. A person "
                  + "needs to decide whether to resend.")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, Exception exception, Guid notificationId, string status);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Notification {NotificationId} is 'sending' — another Worker is sending it right now, or an earlier "
                  + "attempt's outcome is unknown. Not sent again automatically.")]
    private static partial void LogOutcomeUnknown(ILogger logger, Guid notificationId);
}
