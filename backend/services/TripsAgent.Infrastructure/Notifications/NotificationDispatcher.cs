using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>What one attempt to send a notification came to.</summary>
public enum NotificationDispatchOutcome
{
    /// <summary>The provider accepted it.</summary>
    Sent = 1,

    /// <summary>Nothing to do: already finished, unknown, or another Worker holds it right now.</summary>
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
/// <b>Exactly one email per notification, as far as email allows.</b> The row is claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and only while it is still <c>queued</c>, so a redelivered message,
/// a second Worker or a second consumer instance finds nothing to do. The one gap left is the one
/// SMTP itself leaves: if the process dies after the relay accepted the message but before the row
/// commits as sent, the redelivery sends it again. SMTP has no idempotency key to prevent that, and
/// a duplicate receipt is a far smaller harm than a lost booking confirmation.
/// </para>
/// <para>
/// <b>Retries belong to the broker.</b> A transient failure is recorded on the row and reported as
/// <see cref="NotificationDispatchOutcome.RetryLater"/>; the consumer throws, and MassTransit's retry
/// policy for <c>notifications.email</c> redelivers with backoff. The fifth failure marks the row
/// failed and the broker moves the message to <c>notifications.email_error</c> — the dead letter.
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
    TimeProvider clock,
    ILogger<NotificationDispatcher> logger)
{
    /// <summary>Attempts before a notification is marked failed and dead-lettered. Issue #45.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Sends <paramref name="notificationId"/> if it is still waiting to be sent.</summary>
    public async Task<NotificationDispatchOutcome> DispatchAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        using var scope = platformScope.Enter("notification dispatch — the Worker sends every agency's notifications");

        // AddInfrastructure turns on EF's retry-on-transient-failure, which refuses a hand-opened
        // transaction unless the whole unit is handed to it to replay. A replay after the relay
        // accepted the message would send it twice; see the remarks for why that is tolerated.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            notificationId,
            (_, id, token) => DispatchOnceAsync(id, token),
            verifySucceeded: null,
            cancellationToken);
    }

    private async Task<NotificationDispatchOutcome> DispatchOnceAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 'queued' as a literal, like the outbox: a finished notification is not claimable at all.
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
            return NotificationDispatchOutcome.Skipped;
        }

        var outcome = await AttemptAsync(notification, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return outcome;
    }

    private async Task<NotificationDispatchOutcome> AttemptAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (notification.Channel != NotificationChannel.Email)
        {
            notification.RecordFailure($"No provider for the {notification.Channel} channel yet.", giveUp: true);
            return NotificationDispatchOutcome.GaveUp;
        }

        if (await IsSuppressedAsync(notification.RecipientAddress, cancellationToken))
        {
            notification.MarkBounced("The address is on the suppression list after an earlier permanent bounce.", clock.GetUtcNow());
            LogSuppressed(logger, notification.Id);
            return NotificationDispatchOutcome.Bounced;
        }

        var template = await NotificationTemplateSeeder.FindActiveAsync(
            dbContext, notification.TemplateKey, notification.Channel, notification.Locale, cancellationToken);

        if (template is null)
        {
            // Not retried: five more attempts will not seed a template. Running `migrate` will.
            return GiveUp(
                notification,
                $"No active template '{notification.TemplateKey}' for {notification.Channel}/{notification.Locale}. "
                + "Templates are seeded by the migrate command — has it run since this build was deployed?");
        }

        EmailMessage message;
        try
        {
            var brand = await BrandForAsync(notification, template.Audience, cancellationToken);
            var rendered = NotificationRenderer.Render(
                template, brand, notification.RecipientName, Notifier.ReadPayload(notification.Payload));

            message = new EmailMessage(
                notification.RecipientAddress,
                rendered.Subject,
                rendered.Html,
                rendered.Text,

                // A traveller sees the agency's name as the sender, and a reply reaches the agency.
                FromName: template.Audience == NotificationAudience.Traveller ? brand.Name : null,
                ReplyTo: brand.ReplyTo);
        }
        catch (NotificationRenderException ex)
        {
            return GiveUp(notification, ex.Message);
        }

        try
        {
            var receipt = await emailSender.SendAsync(message, cancellationToken);
            notification.MarkSent(template.Version, receipt.ProviderMessageId, clock.GetUtcNow());
            return NotificationDispatchOutcome.Sent;
        }
        catch (EmailRejectedException ex)
        {
            var now = clock.GetUtcNow();
            notification.MarkBounced(ex.Message, now);
            await SuppressAsync(notification, ex.Message, now, cancellationToken);
            LogBounced(logger, ex, notification.Id);
            return NotificationDispatchOutcome.Bounced;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Shutting down is not a failure: that exception escapes, the transaction rolls back and
            // the row stays queued for the redelivery. Anything else counts against the notification.
            var error = $"{ex.GetType().Name}: {ex.Message}";

            if (notification.Attempts + 1 >= MaxAttempts)
            {
                notification.RecordFailure(error, giveUp: true);
                LogGaveUp(logger, ex, notification.Id, notification.TemplateKey, MaxAttempts);
                return NotificationDispatchOutcome.GaveUp;
            }

            notification.RecordFailure(error, giveUp: false);
            LogWillRetry(logger, ex, notification.Id, notification.Attempts);
            return NotificationDispatchOutcome.RetryLater;
        }
    }

    private NotificationDispatchOutcome GiveUp(Notification notification, string reason)
    {
        notification.RecordFailure(reason, giveUp: true);
        LogCannotRender(logger, notification.Id, notification.TemplateKey, reason);
        return NotificationDispatchOutcome.GaveUp;
    }

    /// <summary>
    /// Whose brand wraps the message. Ours for agency staff; the agency's own, always, for a traveller.
    /// </summary>
    private async Task<NotificationBrand> BrandForAsync(
        Notification notification,
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        if (audience == NotificationAudience.AgencyStaff)
        {
            return NotificationBrand.Platform;
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

        return new NotificationBrand(
            agency.TradingName ?? agency.LegalName,
            branding?.PrimaryColor ?? AgencyBranding.DefaultPrimaryColor,

            // Logos are asset ids until the upload pipeline (#18) gives them a public URL. Until
            // then the header shows the agency's name as text, which the renderer handles.
            LogoUrl: null,
            Contact: branding?.ContactAddress,
            ReplyTo: owner);
    }

    private Task<bool> IsSuppressedAsync(string address, CancellationToken cancellationToken) =>
        dbContext.SuppressedEmailAddresses.AnyAsync(
            s => s.Address == address && s.LiftedAt == null,
            cancellationToken);

    private async Task SuppressAsync(Notification notification, string reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!await IsSuppressedAsync(notification.RecipientAddress, cancellationToken))
        {
            dbContext.SuppressedEmailAddresses.Add(
                SuppressedEmailAddress.Create(notification.RecipientAddress, reason, at, notification.Id));
        }
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Notification {NotificationId} bounced permanently; the address is now suppressed.")]
    private static partial void LogBounced(ILogger logger, Exception exception, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Notification {NotificationId} was not sent: its address is suppressed after an earlier bounce.")]
    private static partial void LogSuppressed(ILogger logger, Guid notificationId);
}
