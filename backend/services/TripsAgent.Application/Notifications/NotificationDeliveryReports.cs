using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Notifications;

namespace TripsAgent.Application.Notifications;

/// <summary>What an email provider reported about a message after it had accepted it.</summary>
public enum DeliveryReportKind
{
    /// <summary>It reached the recipient's mailbox.</summary>
    Delivered = 1,

    /// <summary>The mailbox refused it for good, after the provider had accepted it — a hard bounce.</summary>
    Bounced = 2,

    /// <summary>The recipient marked it as spam.</summary>
    Complained = 3,
}

/// <summary>One report from an email provider, in our words rather than the provider's.</summary>
/// <param name="ProviderMessageId">The provider's id for the message — what <see cref="EmailReceipt"/> returned when it was sent.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The provider's own explanation, for the row and for support.</param>
/// <param name="At">When the provider says it happened.</param>
public sealed record DeliveryReport(string ProviderMessageId, DeliveryReportKind Kind, string? Detail, DateTimeOffset At);

/// <summary>What recording a report did.</summary>
public enum DeliveryReportOutcome
{
    /// <summary>The notification, the suppression list, or both, now say what the provider said.</summary>
    Recorded = 1,

    /// <summary>Nothing to change: the report repeats one already recorded, or no longer applies.</summary>
    NothingToChange = 2,

    /// <summary>No notification of ours has that provider message id.</summary>
    UnknownMessage = 3,
}

/// <summary>
/// Records what an email provider says happened to a message after it accepted it — delivered,
/// bounced, or reported as spam. The provider-neutral half of bounce handling (#45).
/// </summary>
/// <remarks>
/// <para>
/// SMTP tells us only that a relay took the message; a refusal during that conversation is handled
/// by <c>NotificationDispatcher</c>. Everything after — the mailbox that turned out not to exist,
/// the reader who pressed "report spam", the confirmation that it arrived — comes later, from the
/// provider, as a webhook or a feed. No provider has been chosen yet (CLAUDE.md, "Stack"), so there
/// is no webhook; the adapter written for whichever one is chosen turns its payload into a
/// <see cref="DeliveryReport"/> and calls this. Nothing here depends on which provider it is.
/// </para>
/// <para>
/// Matched on <see cref="Notification.ProviderMessageId"/>, which is indexed for exactly this. A
/// hard bounce and a complaint both suppress the address, platform-wide: continuing to mail either
/// one damages delivery for every agency. Safe to call more than once for the same report.
/// </para>
/// </remarks>
public sealed partial class NotificationDeliveryReports
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ILogger<NotificationDeliveryReports> _logger;

    public NotificationDeliveryReports(
        IAppDbContext db,
        IPlatformScope platformScope,
        ILogger<NotificationDeliveryReports> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _logger = logger;
    }

    public async Task<DeliveryReportOutcome> RecordAsync(DeliveryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.ProviderMessageId);

        // A provider's report names its own message, not an agency, and arrives with no session.
        using var scope = _platformScope.Enter("email delivery report — a provider names its message, not an agency");

        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.ProviderMessageId == report.ProviderMessageId, cancellationToken);

        if (notification is null)
        {
            LogUnknownMessage(_logger, report.Kind);
            return DeliveryReportOutcome.UnknownMessage;
        }

        // Npgsql refuses an offset other than zero, and a provider's timestamp can carry any.
        var at = report.At.ToUniversalTime();
        var detail = string.IsNullOrWhiteSpace(report.Detail) ? report.Kind.ToString() : report.Detail.Trim();
        var changed = false;

        switch (report.Kind)
        {
            case DeliveryReportKind.Delivered:
                var before = notification.Status;
                notification.MarkDelivered(at);
                changed = notification.Status != before;
                break;

            case DeliveryReportKind.Bounced:
                changed = notification.RecordBounceReport($"Bounced after sending: {detail}");
                changed |= await SuppressAsync(notification, $"Hard bounce: {detail}", at, cancellationToken);
                break;

            case DeliveryReportKind.Complained:
                // The message did arrive, so its status stands; the address still gets no more mail.
                changed = await SuppressAsync(notification, $"Spam complaint: {detail}", at, cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(report), report.Kind, "Unknown delivery report kind.");
        }

        if (!changed)
        {
            return DeliveryReportOutcome.NothingToChange;
        }

        await _db.SaveChangesAsync(cancellationToken);
        LogRecorded(_logger, notification.Id, report.Kind);

        return DeliveryReportOutcome.Recorded;
    }

    private async Task<bool> SuppressAsync(
        Notification notification,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var address = notification.RecipientAddress;

        var alreadySuppressed = _db.SuppressedEmailAddresses.Local.Any(s => s.Address == address && s.IsActive)
            || await _db.SuppressedEmailAddresses.AnyAsync(s => s.Address == address && s.LiftedAt == null, cancellationToken);

        if (alreadySuppressed)
        {
            return false;
        }

        _db.SuppressedEmailAddresses.Add(SuppressedEmailAddress.Create(address, reason, at, notification.Id));
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A {Kind} report named a message we have no record of. Ignored.")]
    private static partial void LogUnknownMessage(ILogger logger, DeliveryReportKind kind);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recorded a {Kind} report for notification {NotificationId}.")]
    private static partial void LogRecorded(ILogger logger, Guid notificationId, DeliveryReportKind kind);
}
