using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// One of an agency's own labels for its products — a category ("Beach holidays") or a theme
/// ("Honeymoon"). The storefront filters on them (FRD §2.12 RS-5).
/// </summary>
/// <remarks>
/// Names are unique per agency and type, ignoring case: the database column is <c>citext</c>, so
/// "Beach" and "beach" cannot both exist as categories. A category and a theme may share a name.
/// </remarks>
public sealed class ProductCategory : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>The longest name. Matches the column.</summary>
    public const int MaxNameLength = 100;

    private ProductCategory() => Name = string.Empty;

    /// <summary>Creates a category for <paramref name="agencyId"/>. Throws when <see cref="Validate"/> would complain.</summary>
    public static ProductCategory Create(Guid agencyId, string name, CategoryType type)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);

        var problems = Validate(name, type);

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems.Select(problem => problem.Message)), nameof(name));
        }

        return new ProductCategory
        {
            AgencyId = agencyId,
            Name = name.Trim(),
            Type = type,
        };
    }

    /// <summary>Every reason a category with this name and type cannot be created.</summary>
    public static IReadOnlyList<ProductProblem> Validate(string? name, CategoryType type)
    {
        var problems = new List<ProductProblem>();

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new("name", "Give the category a name."));
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            problems.Add(new("name", $"Keep the name to {MaxNameLength} characters."));
        }

        if (!Enum.IsDefined(type))
        {
            problems.Add(new("type", "Choose Category or Theme."));
        }

        return problems;
    }

    public Guid AgencyId { get; private set; }

    public string Name { get; private set; }

    public CategoryType Type { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
