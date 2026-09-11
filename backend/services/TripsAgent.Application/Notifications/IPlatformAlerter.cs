namespace TripsAgent.Application.Notifications;

/// <summary>How urgently a platform alert needs a human.</summary>
public enum AlertSeverity
{
    /// <summary>Wake someone up.</summary>
    P1 = 1,

    /// <summary>Needs attention, not at 03:00.</summary>
    P2 = 2,
}

/// <summary>Something the platform team needs to know about.</summary>
/// <param name="Severity">How urgent.</param>
/// <param name="Title">One line. This is what appears in a notification.</param>
/// <param name="Detail">
/// The whole story, for whoever opens it. Written for someone half awake with no context.
/// </param>
/// <param name="Source">Which job or handler raised it, so it can be found in the logs.</param>
/// <param name="AgencyId">
/// The agency it is about, where there is one. A hint for whoever picks it up — plenty of alerts
/// are about the platform itself and carry nothing here.
/// </param>
public sealed record PlatformAlert(
    AlertSeverity Severity,
    string Title,
    string Detail,
    string Source,
    Guid? AgencyId = null);

/// <summary>
/// Raises an alert to the platform team. A port, not a provider.
/// </summary>
/// <remarks>
/// PagerDuty, Opsgenie and Slack are all plausible and none is chosen, so nothing above this may
/// name one. The local implementation logs and emails; swapping it is one registration.
/// </remarks>
public interface IPlatformAlerter
{
    public Task RaiseAsync(PlatformAlert alert, CancellationToken cancellationToken = default);
}
