using System.Text.Json;
using System.Text.Json.Serialization;
using TripsAgent.Application.Messaging;
using TripsAgent.Domain.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Stages events onto the same <see cref="AppDbContext"/> the caller is already using, so they
/// commit atomically with whatever state change produced them. See <see cref="IOutboxWriter"/>.
/// </summary>
/// <remarks>
/// Registered scoped, against the same <see cref="AppDbContext"/> instance the rest of the unit
/// of work uses — that shared instance is the entire mechanism. There is no separate save here on
/// purpose: the row only reaches the database when the caller calls its own
/// <c>SaveChangesAsync</c>.
/// </remarks>
public sealed class OutboxWriter(AppDbContext context, TimeProvider timeProvider) : IOutboxWriter
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        // Enum names, not their numbers, matching AuditSaveChangesInterceptor — a message that
        // waits in the table for a while should not depend on an enum's numeric order never
        // changing between when it was written and when it is dispatched.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public void Enqueue<TEvent>(TEvent domainEvent, Guid? agencyId = null, string? correlationId = null)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // The actual runtime type, not TEvent: a caller holding an event through a base type or
        // an interface must still have it dispatch — and deserialise — as what it really is.
        var runtimeType = domainEvent.GetType();

        var message = OutboxMessage.Create(
            messageType: ShortTypeName(runtimeType),
            payloadJson: JsonSerializer.Serialize(domainEvent, runtimeType, PayloadOptions),
            now: timeProvider.GetUtcNow(),
            agencyId: agencyId,
            correlationId: correlationId);

        context.Add(message);
    }

    /// <summary>
    /// <c>"Namespace.Type, AssemblyName"</c> — no version, culture or public key token. See the
    /// remarks on <see cref="OutboxMessage.MessageType"/> for why the short form matters here.
    /// </summary>
    internal static string ShortTypeName(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";
}
