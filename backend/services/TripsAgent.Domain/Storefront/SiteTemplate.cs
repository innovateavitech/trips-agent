using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// A starter website an agency can begin from: a layout, a typeface, and pages of sample content.
/// </summary>
/// <remarks>
/// <para>
/// Platform reference data with no <c>agency_id</c> — the same templates for every agency. Rows are
/// seeded from the template catalog in code, so a change arrives through a pull request, and the
/// application role can only read them: an agency has no path that writes one.
/// </para>
/// <para>
/// A site copies its template's pages when it is created and never reads them again, so revising a
/// template changes new sites, not existing ones. Only <see cref="Code"/> is read afterwards: it
/// chooses the layout the storefront renders.
/// </para>
/// </remarks>
public sealed class SiteTemplate : Entity, IAuditableEntity
{
    public const int MaxCodeLength = 40;

    private SiteTemplate()
    {
        Code = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
        BlockSchema = "{}";
    }

    public static SiteTemplate Create(string code, string name, string description, int version, string blockSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockSchema);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        return new SiteTemplate
        {
            Code = code.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            Description = description.Trim(),
            Version = version,
            BlockSchema = blockSchema,
            IsActive = true,
        };
    }

    /// <summary>Stable and lower-case — <c>horizon</c>. The storefront renders by it.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    /// <summary>Bumped whenever the catalog's definition changes, so the seeder knows to update.</summary>
    public int Version { get; private set; }

    /// <summary>A picture of the template, when there is one. The console draws its own otherwise.</summary>
    public string? PreviewImageUrl { get; private set; }

    /// <summary>The pages and blocks a new site starts with, as JSON.</summary>
    public string BlockSchema { get; private set; }

    /// <summary>False for a template no longer offered. Sites already made from it keep working.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Replaces the definition with a newer version from the catalog.</summary>
    public void Revise(string name, string description, int version, string blockSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockSchema);

        if (version <= Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A revision must carry a newer version number.");
        }

        Name = name.Trim();
        Description = description.Trim();
        Version = version;
        BlockSchema = blockSchema;
        IsActive = true;
    }

    /// <summary>Stops offering the template to new sites.</summary>
    public void Retire() => IsActive = false;
}

/// <summary>
/// A label no agency may take as its free subdomain — <c>www</c>, <c>admin</c>, <c>support</c> — or a
/// brand whose lookalikes are set aside for review.
/// </summary>
/// <remarks>
/// Data rather than a constant, so the list can grow without a deployment (open question 20). Seeded
/// from <see cref="ReservedHostnames.BaseLabels"/>; readable, never writable, by the application role.
/// </remarks>
public sealed class ReservedHostnameLabel : Entity, IAuditableEntity
{
    private ReservedHostnameLabel()
    {
        Label = string.Empty;
        Reason = string.Empty;
    }

    public static ReservedHostnameLabel Create(string label, ReservedHostnameKind kind, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ReservedHostnameLabel { Label = label.Trim().ToLowerInvariant(), Kind = kind, Reason = reason.Trim() };
    }

    public string Label { get; private set; }

    /// <summary>Refused outright, or accepted and set aside for review.</summary>
    public ReservedHostnameKind Kind { get; private set; }

    public string Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
