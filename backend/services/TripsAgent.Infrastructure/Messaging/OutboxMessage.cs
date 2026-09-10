using System.Diagnostics;
using System.Text.Json;
using TripsAgent.Domain.Common;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// A message waiting in <c>platform.outbox_messages</c> to be published, or the record that it was.
/// </summary>
/// <remarks>
/// <para>
/// The row is written in the same transaction as the business change that produced it, then
/// published by <see cref="OutboxDispatcher"/> in the Worker. That split is the whole point:
/// committing to PostgreSQL and publishing to RabbitMQ cannot be made atomic, but committing two
/// rows to PostgreSQL can. See docs/adr/0005-own-the-transactional-outbox.md.
/// </para>
/// <para>
/// A row moves <c>pending</c> → <c>dispatched</c>, or <c>pending</c> → <c>failed</c> after
/// <see cref="OutboxOptions.MaxAttempts"/> failed publishes. A failed row needs a person; the
/// backlog check keeps reporting it until someone looks.
/// </para>
/// </remarks>
public sealed class OutboxMessage : Entity
{
    /// <summary>Longest error kept in <see cref="LastError"/>. The full exception is in the logs.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>
    /// How payloads are written. Web defaults mean camelCase property names, which is what both
    /// MassTransit and the TypeScript side expect. Public so a consumer can read a payload back with
    /// exactly the settings it was written with.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Private: EF Core binds this to the row's columns, and application code goes through Create.
    private OutboxMessage(
        string messageType,
        string payload,
        DateTimeOffset occurredAt,
        string? correlationId,
        Guid? agencyId)
    {
        MessageType = messageType;
        Payload = payload;
        OccurredAt = occurredAt;
        CorrelationId = correlationId;
        AgencyId = agencyId;
        Status = OutboxMessageStatus.Pending;
        NextAttemptAt = occurredAt;
    }

    /// <summary>The .NET type of the payload, as <c>"Namespace.Type, Assembly"</c>. See <see cref="TypeNameOf"/>.</summary>
    public string MessageType { get; private set; }

    /// <summary>The message as camelCase JSON. Stored as <c>jsonb</c> so it can be queried in psql.</summary>
    public string Payload { get; private set; }

    /// <summary>When the change that produced the message was saved.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Trace id of the request that produced the message, for following it through the logs.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>
    /// The agency the message concerns, for logs and routing. Not a tenancy boundary: the dispatcher
    /// reads every agency's rows, which is why this table has no query filter.
    /// </summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>One of <see cref="OutboxMessageStatus"/>.</summary>
    public string Status { get; private set; }

    /// <summary>Publish attempts so far, successful or not.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>The earliest the dispatcher will try this message again.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>When the broker accepted the message. Null until then.</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>Why the most recent attempt failed, trimmed to <see cref="MaxErrorLength"/>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Wraps <paramref name="message"/> as a pending outbox row.</summary>
    /// <param name="message">A domain event or integration message. Serialised by its runtime type.</param>
    /// <param name="agencyId">The agency it concerns, when there is one.</param>
    /// <param name="occurredAt">Now, from the injected <see cref="TimeProvider"/>.</param>
    public static OutboxMessage Create(object message, Guid? agencyId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        // GetType(), not the static type. Serialising through IDomainEvent — an interface with no
        // properties — writes "{}" and silently throws the event's data away.
        var type = message.GetType();

        return new OutboxMessage(
            TypeNameOf(type),
            JsonSerializer.Serialize(message, type, JsonOptions),
            occurredAt.ToUniversalTime(),
            Activity.Current?.Id,
            agencyId);
    }

    /// <summary>The name a message type is stored under: <c>"Namespace.Type, Assembly"</c>.</summary>
    /// <remarks>
    /// Enough for <see cref="Type.GetType(string)"/> to find the type in another assembly, without a
    /// version number — so deploying a new build does not orphan messages written by the old one.
    /// Renaming or moving an event class <em>does</em> orphan them, so do not do that while
    /// messages of that type may still be waiting.
    /// </remarks>
    public static string TypeNameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"{type.FullName ?? type.Name}, {type.Assembly.GetName().Name}";
    }

    /// <summary>The broker accepted the message. It will not be published again.</summary>
    public void MarkDispatched(DateTimeOffset dispatchedAt)
    {
        AttemptCount++;
        Status = OutboxMessageStatus.Dispatched;
        DispatchedAt = dispatchedAt;
        LastError = null;
    }

    /// <summary>A publish attempt failed.</summary>
    /// <param name="error">What went wrong. Trimmed to <see cref="MaxErrorLength"/>.</param>
    /// <param name="retryAt">When to try again, or null to give up and mark the message failed.</param>
    public void RecordFailure(string error, DateTimeOffset? retryAt)
    {
        ArgumentNullException.ThrowIfNull(error);

        AttemptCount++;
        LastError = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];

        if (retryAt is { } next)
        {
            NextAttemptAt = next;
        }
        else
        {
            Status = OutboxMessageStatus.Failed;
        }
    }
}

/// <summary>
/// The values of <c>outbox_messages.status</c>. Plain strings rather than an enum so they read the
/// same in psql as in C#, and so the partial indexes can name them literally.
/// </summary>
public static class OutboxMessageStatus
{
    /// <summary>Waiting to be published, possibly after a failed attempt.</summary>
    public const string Pending = "pending";

    /// <summary>Accepted by the broker. Done.</summary>
    public const string Dispatched = "dispatched";

    /// <summary>Gave up after too many failed attempts. Needs a person.</summary>
    public const string Failed = "failed";
}
