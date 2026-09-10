using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TripsAgent.Application.Auditing;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// Writes an <see cref="AuditLogEntry"/> for every insert, update and delete of an
/// <see cref="IAuditLogged"/> entity.
///
/// This sits in the save pipeline rather than in each handler on purpose. A handler that has to
/// remember to log is a handler that will one day forget, and the one change nobody logged is
/// invariably the one somebody later needs to explain. Here the audit row joins the same
/// <c>SaveChanges</c>, inside the same transaction: either both the change and its record land,
/// or neither does.
///
/// Registered scoped — one per request — so it always attributes changes to the current actor.
/// </summary>
public sealed class AuditSaveChangesInterceptor(
    IAuditContext auditContext,
    AuditRedactionPolicy redactionPolicy,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions StateSerializerOptions = new()
    {
        // Enum names, not their numbers: a reader six months from now should not need the
        // enum definition at the revision it had when the row was written.
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>
    /// Audit rows this interceptor has added that have not been saved yet.
    ///
    /// Tracked so a failed save that is tried again does not record everything twice. When
    /// SaveChanges throws, EF leaves every entity — including the audit rows added here — in
    /// the change tracker, still marked Added. A caller that fixes the problem and saves again
    /// would otherwise write the stale rows from the failed attempt alongside fresh ones.
    /// </summary>
    private readonly List<AuditLogEntry> unsaved = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        CaptureAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        CaptureAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        unsaved.Clear();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        unsaved.Clear();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureAuditEntries(DbContext? context)
    {
        if (context is null || context.Model.FindEntityType(typeof(AuditLogEntry)) is null)
        {
            return;
        }

        DiscardRowsFromFailedAttempt(context);

        // Materialised before anything is added: adding to the change tracker while enumerating
        // it throws, and the audit rows themselves are about to be added.
        var audited = context.ChangeTracker
            .Entries<IAuditLogged>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (audited.Count == 0)
        {
            return;
        }

        var occurredAt = timeProvider.GetUtcNow();
        var entries = audited
            .Select(entry => BuildEntry(entry, occurredAt))
            .OfType<AuditLogEntry>()
            .ToList();

        if (entries.Count > 0)
        {
            context.Set<AuditLogEntry>().AddRange(entries);
            unsaved.AddRange(entries);
        }
    }

    /// <summary>
    /// Drops the audit rows a previous, failed save added. The entities they describe are still
    /// tracked with their pending changes, so they are about to be re-audited from current state.
    /// </summary>
    private void DiscardRowsFromFailedAttempt(DbContext context)
    {
        foreach (var stale in unsaved)
        {
            var entry = context.Entry(stale);
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }

        unsaved.Clear();
    }

    private AuditLogEntry? BuildEntry(EntityEntry<IAuditLogged> entry, DateTimeOffset occurredAt)
    {
        var (action, before, after) = entry.State switch
        {
            EntityState.Added => (AuditActions.Created, null, SnapshotAll(entry, current: true)),
            EntityState.Deleted => (AuditActions.Deleted, SnapshotAll(entry, current: false), null),
            _ => SnapshotChanges(entry),
        };

        // A "modified" entity whose values all came back the same is not a change worth a row.
        if (before is null && after is null)
        {
            return null;
        }

        return new AuditLogEntry
        {
            OccurredAt = occurredAt,
            AgencyId = ResolveAgencyId(entry),
            ActorUserId = auditContext.ActorUserId,
            ActorType = auditContext.ActorType,
            ActorIpAddress = auditContext.ActorIpAddress,
            Action = action,
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = ResolveEntityId(entry),
            BeforeState = before,
            AfterState = after,
            Reason = auditContext.Reason,
            CorrelationId = auditContext.CorrelationId,
        };
    }

    /// <summary>
    /// The whole row, for an insert or a delete — there is no "before" or "after" to diff against.
    /// </summary>
    private string SnapshotAll(EntityEntry<IAuditLogged> entry, bool current)
    {
        var state = entry.Properties.ToDictionary(
            property => property.Metadata.Name,
            property => Redact(property, current ? property.CurrentValue : property.OriginalValue),
            StringComparer.Ordinal);

        return JsonSerializer.Serialize(state, StateSerializerOptions);
    }

    /// <summary>
    /// Only the columns that actually changed.
    ///
    /// Recording the entire row on both sides of every update would make the reader diff two
    /// large objects by eye to find the one field that moved. The question an audit log answers
    /// is "what changed", so that is what it stores.
    /// </summary>
    private (string Action, string? Before, string? After) SnapshotChanges(EntityEntry<IAuditLogged> entry)
    {
        var changed = entry.Properties
            .Where(property => property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
            .ToList();

        if (changed.Count == 0)
        {
            return (AuditActions.Updated, null, null);
        }

        var before = changed.ToDictionary(
            property => property.Metadata.Name,
            property => Redact(property, property.OriginalValue),
            StringComparer.Ordinal);

        var after = changed.ToDictionary(
            property => property.Metadata.Name,
            property => Redact(property, property.CurrentValue),
            StringComparer.Ordinal);

        return (
            AuditActions.Updated,
            JsonSerializer.Serialize(before, StateSerializerOptions),
            JsonSerializer.Serialize(after, StateSerializerOptions));
    }

    private object? Redact(PropertyEntry property, object? value)
    {
        // A byte array is a document, an avatar or a PDF. Its length is audit-worthy; base64 of
        // its contents would bloat every row and copy the file into a table nobody can delete from.
        if (value is byte[] bytes)
        {
            return $"[binary: {bytes.Length} bytes]";
        }

        return redactionPolicy.Apply(property.Metadata.Name, value);
    }

    /// <summary>
    /// Prefers the agency the record itself belongs to over the one the actor is signed in as, so
    /// a platform admin acting on an agency's data produces a row that agency can still see.
    /// </summary>
    private Guid? ResolveAgencyId(EntityEntry<IAuditLogged> entry) =>
        entry.Entity is ITenantOwnedEntity owned ? owned.AgencyId : auditContext.AgencyId;

    /// <summary>
    /// The key as text. Composite keys are joined, so any key shape fits one column and the
    /// audit log never needs a schema change when an entity's key does.
    /// </summary>
    private static string ResolveEntityId(EntityEntry<IAuditLogged> entry)
    {
        var key = entry.Metadata.FindPrimaryKey();

        if (key is null)
        {
            return string.Empty;
        }

        var values = key.Properties
            .Select(property => entry.Property(property.Name).CurrentValue?.ToString() ?? string.Empty);

        return string.Join('|', values);
    }
}
