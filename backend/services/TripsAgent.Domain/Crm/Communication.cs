using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Crm;

/// <summary>
/// One entry on a customer's timeline — an email sent, a call taken, a WhatsApp message, a note:
/// <c>crm.communications</c>.
/// </summary>
/// <remarks>
/// <para>
/// Most are logged by the agent after the fact. The platform logs its own too: the storefront's trip
/// request as it arrives, a quote as it is emailed, and the customer's answer to it. SMS and WhatsApp
/// are only ever logged, never sent from here (build plan, "What the MVP leaves out").
/// </para>
/// <para>
/// Append-only for the application role: a message, once logged, is a record of what was said. Like a
/// task it keeps its customer, and its lead where there is one, so each timeline is one indexed read.
/// </para>
/// </remarks>
public sealed class Communication : Entity, ITenantScoped
{
    private Communication()
    {
        Summary = string.Empty;
        ByName = string.Empty;
    }

    /// <summary>Logs a message. Throws on a summary <see cref="Check"/> refuses: the application checks first.</summary>
    /// <param name="byName">
    /// Who it came from, as their name read now: the customer for an inbound message, the person at the
    /// agency for an outbound one.
    /// </param>
    public static Communication Log(
        Guid agencyId,
        CommunicationChannel channel,
        CommunicationDirection direction,
        string summary,
        CrmRecordType relatedType,
        Guid relatedId,
        Guid customerId,
        Guid? leadId,
        Guid? byUserId,
        string byName,
        DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(relatedId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(byName);

        if (Check(summary) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message, nameof(summary));
        }

        if (!Enum.IsDefined(channel) || !Enum.IsDefined(direction) || !Enum.IsDefined(relatedType))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Not a channel, direction or record type.");
        }

        return new Communication
        {
            AgencyId = agencyId,
            Channel = channel,
            Direction = direction,
            Summary = summary.Trim(),
            RelatedType = relatedType,
            RelatedId = relatedId,
            CustomerId = customerId,
            LeadId = leadId,
            ByUserId = byUserId,
            ByName = byName.Trim(),
            At = at.ToUniversalTime(),
        };
    }

    /// <summary>Every reason <paramref name="summary"/> cannot be logged. Empty when it can.</summary>
    public static IReadOnlyList<CrmProblem> Check(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return [new("summary", "Say what was said.")];
        }

        if (summary.Trim().Length > CrmLimits.MaxSummaryLength)
        {
            return
            [
                new("summary", string.Create(
                    CultureInfo.InvariantCulture, $"Keep it to {CrmLimits.MaxSummaryLength} characters.")),
            ];
        }

        return [];
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public CommunicationChannel Channel { get; private set; }

    public CommunicationDirection Direction { get; private set; }

    public string Summary { get; private set; }

    public CrmRecordType RelatedType { get; private set; }

    public Guid RelatedId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid? LeadId { get; private set; }

    /// <summary>The person at the agency who logged or sent it. Null when the platform logged it.</summary>
    public Guid? ByUserId { get; private set; }

    public string ByName { get; private set; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; private set; }
}
