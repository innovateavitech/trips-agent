using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.Application.Catalog;

/// <summary>What came of creating a category.</summary>
public abstract record CategoryCreateOutcome
{
    private CategoryCreateOutcome()
    {
    }

    public sealed record Created(ProductCategory Category) : CategoryCreateOutcome;

    /// <summary>The name or type will not do. <paramref name="Problems"/> lists every reason.</summary>
    public sealed record Invalid(IReadOnlyList<ProductProblem> Problems) : CategoryCreateOutcome;

    /// <summary>The agency already has a category of this type with this name, ignoring case.</summary>
    public sealed record Duplicate(string Name, CategoryType Type) : CategoryCreateOutcome;
}

/// <summary>The calling agency's own categories and themes, which its products are tagged with.</summary>
public sealed class ProductCategoryService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IUniqueViolationDetector _uniqueViolations;

    public ProductCategoryService(IAppDbContext db, ITenantContext tenant, IUniqueViolationDetector uniqueViolations)
    {
        _db = db;
        _tenant = tenant;
        _uniqueViolations = uniqueViolations;
    }

    /// <summary>Categories first, then themes, each alphabetically.</summary>
    public async Task<IReadOnlyList<ProductCategory>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.ProductCategories
            .AsNoTracking()
            .OrderBy(category => category.Type)
            .ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);

    public async Task<CategoryCreateOutcome> CreateAsync(
        string? name,
        CategoryType type,
        CancellationToken cancellationToken = default)
    {
        var agencyId = _tenant.AgencyId ?? throw new InvalidOperationException(
            "Categories belong to an agency, and none is resolved for this request.");

        var problems = ProductCategory.Validate(name, type);

        if (problems.Count > 0)
        {
            return new CategoryCreateOutcome.Invalid(problems);
        }

        var category = ProductCategory.Create(agencyId, name!, type);
        _db.ProductCategories.Add(category);

        // The unique index decides, not a read beforehand: it is case-insensitive (citext) and it
        // settles two people creating "Beach" at the same moment, which a read-then-write cannot.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return new CategoryCreateOutcome.Duplicate(category.Name, type);
        }

        return new CategoryCreateOutcome.Created(category);
    }
}
