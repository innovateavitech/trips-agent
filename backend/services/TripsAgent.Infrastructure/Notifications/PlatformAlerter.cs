using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Platform;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>Where P1 alerts are emailed, until a pager is chosen.</summary>
public sealed class PlatformAlertOptions
{
    public const string SectionName = "Alerting";

    /// <summary>
    /// Who gets the email. Empty means nobody does.
    /// </summary>
    /// <remarks>
    /// Empty is a legitimate configuration — locally there is nowhere to send it — so a missing
    /// address is not an error. It is logged at Warning for a P1, though, because a production
    /// environment where nobody is emailed is worth noticing before the first real incident.
    /// </remarks>
    public string? P1Recipient { get; init; }
}

/// <summary>
/// Raises platform alerts three ways: the log, the back-office queue, and email.
/// </summary>
/// <remarks>
/// <para>
/// Three, because they have different readers and different failure modes. The log is what an
/// engineer greps during an incident. <c>platform.admin_alerts</c> is what the back-office widget
/// shows, so an alert raised at 03:00 is still visible at 09:00 to someone who was asleep. The
/// email is the only one that actively interrupts a person.
/// </para>
/// <para>
/// A pager — PagerDuty, Opsgenie — belongs here too and none is chosen yet, which is why this
/// sits behind <see cref="IPlatformAlerter"/>. Swapping or adding one is a change to this class
/// and nothing above it.
/// </para>
/// <para>
/// Nothing here throws. An alert that fails to send must not take down the job that raised it:
/// the job found something wrong and its own record of that is more important than the
/// notification about it.
/// </para>
/// </remarks>
public sealed partial class PlatformAlerter : IPlatformAlerter
{
    private readonly IAppDbContext _db;
    private readonly IEmailSender _email;
    private readonly PlatformAlertOptions _options;
    private readonly ILogger<PlatformAlerter> _logger;

    public PlatformAlerter(
        IAppDbContext db,
        IEmailSender email,
        PlatformAlertOptions options,
        ILogger<PlatformAlerter> logger)
    {
        _db = db;
        _email = email;
        _options = options;
        _logger = logger;
    }

    public async Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (alert.Severity == AlertSeverity.P1)
        {
            LogP1(_logger, alert.Source, alert.Title, alert.Detail);
        }
        else
        {
            LogP2(_logger, alert.Source, alert.Title, alert.Detail);
        }

        await QueueForBackOfficeAsync(alert, cancellationToken);
        await EmailAsync(alert, cancellationToken);
    }

    private async Task QueueForBackOfficeAsync(PlatformAlert alert, CancellationToken cancellationToken)
    {
        try
        {
            _db.AdminAlerts.Add(AdminAlert.ForPlatform(
                TypeOf(alert.Source),
                alert.Severity == AlertSeverity.P1 ? AdminAlertSeverity.Critical : AdminAlertSeverity.Warning,
                alert.Title,
                alert.Source,
                alert.AgencyId));

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already logged above, which is the part that matters.
            LogQueueFailed(_logger, ex, alert.Source);
        }
    }

    private async Task EmailAsync(PlatformAlert alert, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.P1Recipient))
        {
            if (alert.Severity == AlertSeverity.P1)
            {
                LogNoRecipient(_logger, alert.Title);
            }

            return;
        }

        var label = alert.Severity == AlertSeverity.P1 ? "P1" : "P2";

        try
        {
            await _email.SendAsync(
                new EmailMessage(
                    _options.P1Recipient,
                    $"[{label}] {alert.Title}",
                    $"<h2>{System.Net.WebUtility.HtmlEncode(alert.Title)}</h2>"
                    + $"<pre>{System.Net.WebUtility.HtmlEncode(alert.Detail)}</pre>"
                    + $"<p>Raised by {System.Net.WebUtility.HtmlEncode(alert.Source)}.</p>",
                    $"{alert.Title}\n\n{alert.Detail}\n\nRaised by {alert.Source}."),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEmailFailed(_logger, ex, alert.Title);
        }
    }

    /// <summary>
    /// Maps the raising job onto an alert type for the back-office queue.
    /// </summary>
    /// <remarks>
    /// A small lookup rather than a field on <see cref="PlatformAlert"/>, so that Application does
    /// not have to know the back-office taxonomy — which is a persistence concern.
    /// </remarks>
    private static AdminAlertType TypeOf(string source) => source switch
    {
        "LedgerIntegrityAudit" => AdminAlertType.LedgerIntegrity,
        Application.Suppliers.SupplierBookingStatusPoller.AlertSource => AdminAlertType.SupplierBookingError,
        Application.Suppliers.SupplierBookingStatusPoller.TimeLimitAlertSource => AdminAlertType.TicketTimeLimitBreach,
        Application.Storefront.CertificateSweep.AlertSource => AdminAlertType.SiteCertificate,
        _ => AdminAlertType.GatewayError,
    };

    [LoggerMessage(Level = LogLevel.Critical, Message = "P1 from {Source}: {Title}\n{Detail}")]
    private static partial void LogP1(ILogger logger, string source, string title, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "P2 from {Source}: {Title}\n{Detail}")]
    private static partial void LogP2(ILogger logger, string source, string title, string detail);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not queue the alert from {Source} for the back office. It is in the log above.")]
    private static partial void LogQueueFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not email the alert \"{Title}\".")]
    private static partial void LogEmailFailed(ILogger logger, Exception exception, string title);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A P1 was raised (\"{Title}\") but Alerting:P1Recipient is not configured, so nobody was emailed.")]
    private static partial void LogNoRecipient(ILogger logger, string title);
}
