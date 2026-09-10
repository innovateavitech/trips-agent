using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Messaging;

/// <summary>
/// One event captured inside a business transaction, waiting to be published to the broker.
///
/// This is what closes the dual-write gap: a handler that changes state and wants the rest of
/// the platform to know adds one of these to the same <c>DbContext</c> it is already using, and
/// calls <c>SaveChangesAsync</c> once. Either both the state change and this row land, or
/// neither does — there is no window where the database says one thing and a message already on
/// the broker says another.
///
/// A separate dispatcher (issue #30, <c>OutboxDispatcher</c>) polls for rows with no
/// <see cref="DispatchedAt"/>, publishes them, and marks them dispatched. If the process dies
/// between committing this row and marking it dispatched, the row is still here on restart and
/// gets published then — <em>at least once</em>, never zero times. A consumer seeing the same
/// message twice is expected, not a bug; see <c>InboxMessage</c>.
/// </summary>
public sealed class OutboxMessage : Entity
{
    /// <summary>Longest an error message is kept. Long enough to identify the failure, short enough that one poison message cannot bloat the table.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>EF Core materialises through this, using its own reflection — not the API below.</summary>
    private OutboxMessage()
    {
    }

    /// <summary>When this row was written — the moment the business transaction committed, not when it is eventually published.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The agency this event belongs to, or null for a platform-wide event.</summary>
    public Guid? AgencyId { get; init; }

    /// <summary>
    /// The event's CLR type, in the short form <c>Type.GetType</c> accepts: <c>"Namespace.Type, AssemblyName"</c>
    /// with no version, culture or public key token.
    /// </summary>
    /// <remarks>
    /// The short form is deliberate. The Api and the Worker are scaled and deployed
    /// independently, so the assembly that wrote this row and the assembly that reads it back
    /// to dispatch it are not guaranteed to be the exact same build. A strong, versioned name
    /// would fail to resolve the moment the two drift by even a patch release; the short form
    /// resolves against whatever version of the assembly the dispatching process has loaded.
    /// </remarks>
    public required string MessageType { get; init; }

    /// <summary>The event, serialised as JSON. Deserialised back to <see cref="MessageType"/> at dispatch time.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>Ties this event to the request or job that produced it.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>When this row was successfully published. Null means still pending.</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>How many publish attempts have failed. Zero until the first failure.</summary>
    public int Attempts { get; private set; }

    /// <summary>The most recent failure, if any. Cleared once the message dispatches.</summary>
    public string? LastError { get; private set; }

    /// <summary>Earliest the next attempt may run. Null means eligible immediately.</summary>
    public DateTimeOffset? NotBefore { get; private set; }

    /// <summary>True once this event has been handed to the broker.</summary>
    public bool IsDispatched => DispatchedAt is not null;

    /// <summary>
    /// Stages one event for publishing. Add the result to the same <c>DbContext</c> the caller
    /// is already using and call <c>SaveChangesAsync</c> — do not call this and save separately,
    /// or the transactional guarantee this type exists for is lost.
    /// </summary>
    /// <param name="messageType">
    /// The short-form type name — see <see cref="MessageType"/>. Infrastructure computes this
    /// from the actual runtime type of the event, not the compile-time type parameter, so a
    /// caller publishing through a base type still dispatches as the real subtype.
    /// </param>
    /// <param name="payloadJson">The event, already serialised.</param>
    /// <param name="now">The transaction's instant. From the injected clock, never <c>DateTimeOffset.UtcNow</c>.</param>
    public static OutboxMessage Create(
        string messageType,
        string payloadJson,
        DateTimeOffset now,
        Guid? agencyId = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        return new OutboxMessage
        {
            OccurredAt = now,
            AgencyId = agencyId,
            MessageType = messageType,
            PayloadJson = payloadJson,
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Records a successful publish. Idempotent — calling it twice (a dispatcher retried after a
    /// crash between publish and this call) leaves the first <see cref="DispatchedAt"/> in place
    /// rather than moving it forward, so the timestamp still answers "when did this first go out".
    /// </summary>
    public void MarkDispatched(DateTimeOffset now)
    {
        if (IsDispatched)
        {
            return;
        }

        DispatchedAt = now;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt and sets when the next one may run.
    /// </summary>
    /// <param name="error">What went wrong. Truncated to <see cref="MaxErrorLength"/>.</param>
    /// <param name="now">The instant of this attempt.</param>
    /// <param name="backoff">How long to wait before trying again.</param>
    public void RecordFailedAttempt(string error, DateTimeOffset now, TimeSpan backoff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        Attempts++;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        NotBefore = now + backoff;
    }
}
