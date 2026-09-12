using System.Diagnostics.CodeAnalysis;

namespace TripsAgent.Application.Messaging;

/// <summary>
/// The named queues from the delivery plan §3, as objects rather than loose strings.
///
/// Why a class and not a <c>const string</c> or an enum: a queue name is typed once, here, and
/// every caller picks from this list. A typo in a queue name does not fail the build and does not
/// throw — RabbitMQ happily creates the misspelled queue, the message lands in it, and nothing
/// ever reads it again. That failure is invisible until someone asks why a customer never got
/// their ticket.
///
/// An enum would be safer than strings but would not carry the wire name, and the wire name is
/// what a developer sees in the RabbitMQ management UI at http://localhost:15672. Keeping the two
/// together means the code and the console agree.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "CA1711 reserves 'Queue' for collection types. This is the name of a RabbitMQ "
                    + "queue, which is the domain word everyone uses — renaming it to dodge the rule "
                    + "would make the code disagree with the broker's own UI.")]
public sealed class MessageQueue
{
    private MessageQueue(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Suffix MassTransit appends when a message has exhausted its retries. Not configurable in
    /// MassTransit v8 without writing a custom formatter, so the plan's phrase "a dead-letter
    /// queue per queue" is spelled <c>booking.saga_error</c> on the broker, not
    /// <c>booking.saga.dead-letter</c>.
    /// </summary>
    public const string DeadLetterSuffix = "_error";

    /// <summary>
    /// Suffix MassTransit uses for messages that arrived at an endpoint with no consumer for
    /// their type. Worth knowing about: a message here was <em>discarded</em>, not retried.
    /// </summary>
    public const string SkippedSuffix = "_skipped";

    // ---------------------------------------------------------------------------------------
    // The money and ticket path. Plan §3, "Critical path".
    // ---------------------------------------------------------------------------------------

    /// <summary>The checkout state machine. Highest priority: it is holding someone's money.</summary>
    public static MessageQueue BookingSaga { get; } = new("booking.saga");

    /// <summary>Supplier booking-status polling. There are no webhooks, so this is how we learn outcomes.</summary>
    public static MessageQueue SupplierPoll { get; } = new("supplier.poll");

    /// <summary>Paystack webhook processing.</summary>
    public static MessageQueue PaymentsWebhook { get; } = new("payments.webhook");

    /// <summary>Refunds and wallet credits when a booking fails after payment.</summary>
    public static MessageQueue PaymentsReversal { get; } = new("payments.reversal");

    // ---------------------------------------------------------------------------------------
    // Everything else.
    // ---------------------------------------------------------------------------------------

    /// <summary>Transactional email.</summary>
    public static MessageQueue NotificationsEmail { get; } = new("notifications.email");

    /// <summary>Transactional SMS.</summary>
    public static MessageQueue NotificationsSms { get; } = new("notifications.sms");

    /// <summary>Invoice and voucher PDF rendering.</summary>
    public static MessageQueue DocumentsRender { get; } = new("documents.render");

    /// <summary>Uploaded asset processing: virus scan, EXIF strip, resize.</summary>
    public static MessageQueue MediaProcess { get; } = new("media.process");

    /// <summary>Long-running report generation. Deliberately isolated so a slow report cannot starve the rest.</summary>
    public static MessageQueue ReportsGenerate { get; } = new("reports.generate");

    /// <summary>Custom-domain DNS verification and SSL issuance.</summary>
    public static MessageQueue DomainsProvision { get; } = new("domains.provision");

    /// <summary>Analytics rollups into the fact and aggregate tables.</summary>
    public static MessageQueue AnalyticsRollup { get; } = new("analytics.rollup");

    /// <summary>The name on the wire, exactly as it appears in the RabbitMQ management UI.</summary>
    public string Name { get; }

    /// <summary>Where MassTransit parks a message that has run out of retries.</summary>
    public string DeadLetterName => Name + DeadLetterSuffix;

    /// <summary>
    /// Every queue, in the plan's order.
    ///
    /// Declared last on purpose. Static property initialisers run top to bottom, so this can only
    /// see the queues declared above it — put a new queue above this line, then add it here.
    /// <c>MessageQueueTests</c> fails if you forget the second half.
    /// </summary>
    public static IReadOnlyList<MessageQueue> All { get; } =
    [
        BookingSaga,
        SupplierPoll,
        PaymentsWebhook,
        PaymentsReversal,
        NotificationsEmail,
        NotificationsSms,
        DocumentsRender,
        MediaProcess,
        ReportsGenerate,
        DomainsProvision,
        AnalyticsRollup,
    ];

    /// <inheritdoc />
    public override string ToString() => Name;
}
