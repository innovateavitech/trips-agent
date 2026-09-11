using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Storefront;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Storefront;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Storefront;

/// <summary>Wires the website builder: where sites live, and the row lock publishing takes.</summary>
public static class StorefrontRegistration
{
    public static IServiceCollection AddStorefront(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ReadOptions(configuration));
        services.AddScoped<ISiteLock, SiteLock>();

        return services;
    }

    /// <summary>
    /// Reads <c>Storefront:*</c>. Anything unset keeps its local default; anything set wrongly stops
    /// startup, because a typo in the base domain would hand every new agency an address that does not work.
    /// </summary>
    public static StorefrontOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(StorefrontOptions.SectionName);
        var defaults = new StorefrontOptions();

        var format = string.IsNullOrWhiteSpace(section["SiteUrlFormat"]) ? defaults.SiteUrlFormat : section["SiteUrlFormat"]!.Trim();

        if (!format.Contains("{host}", StringComparison.Ordinal)
            || !(format.StartsWith("https://", StringComparison.Ordinal) || format.StartsWith("http://", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{StorefrontOptions.SectionName}:SiteUrlFormat must be an address with {{host}} in it, like https://{{host}}. It was '{format}'.");
        }

        return new StorefrontOptions
        {
            SubdomainBaseDomain = Hostname(section["SubdomainBaseDomain"], defaults.SubdomainBaseDomain, "SubdomainBaseDomain"),
            CustomDomainTarget = Hostname(section["CustomDomainTarget"], defaults.CustomDomainTarget, "CustomDomainTarget"),
            SiteUrlFormat = format,
            PreviewLinkLifetime = Lifetime(section["PreviewLinkLifetime"], defaults.PreviewLinkLifetime),
        };
    }

    private static string Hostname(string? value, string fallback, string key)
    {
        var chosen = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        return Hostnames.NormaliseHostHeader(chosen) ?? throw new InvalidOperationException(
            $"{StorefrontOptions.SectionName}:{key} must be a hostname, like sites.example.com. It was '{chosen}'.");
    }

    private static TimeSpan Lifetime(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var lifetime)
            || lifetime <= TimeSpan.Zero
            || lifetime > StorefrontOptions.MaxPreviewLinkLifetime)
        {
            throw new InvalidOperationException(
                $"{StorefrontOptions.SectionName}:PreviewLinkLifetime must be more than zero and at most a day (1.00:00:00). It was '{value}'.");
        }

        return lifetime;
    }
}

/// <summary>
/// <see cref="ISiteLock"/> as <c>SELECT … FOR UPDATE</c> on the agency's site row, on the request's own
/// connection and transaction.
/// </summary>
public sealed class SiteLock : ISiteLock
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public SiteLock(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<Guid?> LockCurrentSiteAsync(CancellationToken cancellationToken = default)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The site lock only means something inside a transaction; take it through ITransactionRunner.");
        }

        var agencyId = _tenant.AgencyId ?? throw new InvalidOperationException(
            "The site lock is taken for an agency, and none is resolved for this request.");

        // Named by agency as well as policed by row-level security, so even a connection that bypasses
        // the policies locks only this agency's row.
        var ids = await _db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM storefront.sites WHERE agency_id = {agencyId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : ids[0];
    }
}

/// <summary>
/// Copies the template catalog and the hostname denylist into their tables. Run by <c>migrate</c>,
/// through <see cref="ReferenceDataSeeder"/>.
/// </summary>
/// <remarks>
/// Additive: a template is inserted when missing and revised when the catalog carries a newer version;
/// a denylisted label is inserted when missing and never removed, so a label added by hand stays.
/// </remarks>
public static class StorefrontReferenceData
{
    private const string BrandReason = "A well-known brand. A lookalike claim is set aside for a person to review.";

    public static async Task EnsureAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var templates = await dbContext.SiteTemplates.ToListAsync(cancellationToken);

        foreach (var definition in SiteTemplateCatalog.All)
        {
            var schema = SiteTemplateCatalog.SchemaJson(definition);
            var row = templates.FirstOrDefault(template => template.Code == definition.Code);

            if (row is null)
            {
                dbContext.SiteTemplates.Add(SiteTemplate.Create(definition.Code, definition.Name, definition.Description, definition.Version, schema));
            }
            else if (row.Version < definition.Version)
            {
                row.Revise(definition.Name, definition.Description, definition.Version, schema);
            }
        }

        var labels = (await dbContext.ReservedHostnameLabels.Select(label => label.Label).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (label, reason) in ReservedHostnames.BaseLabels.Where(entry => labels.Add(entry.Label)))
        {
            dbContext.ReservedHostnameLabels.Add(ReservedHostnameLabel.Create(label, ReservedHostnameKind.Reserved, reason));
        }

        foreach (var brand in ReservedHostnames.KnownBrands.Where(labels.Add))
        {
            dbContext.ReservedHostnameLabels.Add(ReservedHostnameLabel.Create(brand, ReservedHostnameKind.Brand, BrandReason));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
