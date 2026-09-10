namespace TripsAgent.Domain.Common;

/// <summary>
/// Base class for anything with its own identity and lifecycle — an agency, a user, an order.
/// </summary>
/// <remarks>
/// <para>
/// Identity is a UUID v7 rather than a database sequence. Two reasons that matter here:
/// the application can assign the id <i>before</i> the row is inserted (so an aggregate can
/// reference its children inside one transaction), and v7 embeds a timestamp in its high bits,
/// so rows land in roughly insert order and the B-tree index does not fragment the way random
/// v4 keys do.
/// </para>
/// <para>
/// Equality is by id, not by reference or by field. Two instances loaded in different
/// <c>DbContext</c>s represent the same thing if their ids match.
/// </para>
/// </remarks>
public abstract class Entity
{
    /// <summary>Creates an entity with a freshly generated, time-ordered identity.</summary>
    protected Entity() => Id = Guid.CreateVersion7();

    /// <summary>
    /// Rehydration constructor for EF Core and for tests that need a deterministic id.
    /// </summary>
    protected Entity(Guid id) => Id = id;

    public Guid Id { get; protected set; }

    public override bool Equals(object? obj) =>
        obj is Entity other
        && other.GetType() == GetType()
        && other.Id == Id
        && Id != Guid.Empty;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// Marks an entity whose rows record when they were created and last changed.
/// <c>AppDbContext</c> stamps both on save, so no handler ever sets them by hand and no row
/// can quietly skip the audit trail.
/// </summary>
/// <remarks>
/// Both are <see cref="DateTimeOffset"/>, never <c>DateTime</c>. A <c>DateTime</c> carries no
/// offset, so "was this ticket issued before the time limit?" becomes unanswerable the moment
/// two servers disagree about their local zone. The database column is
/// <c>timestamp with time zone</c> and always holds UTC.
/// </remarks>
public interface IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Marks a row that belongs to exactly one agency. Every business table implements this, and
/// EF Core adds a global query filter for it so a forgotten <c>WHERE agency_id = …</c> cannot
/// leak one travel agency's customers and prices to another.
/// </summary>
/// <remarks>
/// The filter is applied automatically — see CLAUDE.md rule 3. Do not reach for
/// <c>IgnoreQueryFilters()</c> to work around it; ask first.
/// </remarks>
public interface ITenantOwnedEntity
{
    public Guid AgencyId { get; }
}
