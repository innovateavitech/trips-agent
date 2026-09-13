using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Catalog;

/// <summary>Shared constants for the catalog tables.</summary>
public static class CatalogSchema
{
    /// <summary>The PostgreSQL schema holding agent-authored products: tours, packages and visas.</summary>
    public const string Name = "catalog";
}

/// <summary>
/// <c>catalog.products</c>. The shape CHECKs, row-level security and grants are in the migration,
/// as hand-written SQL: EF Core can express none of them.
/// </summary>
/// <remarks>
/// <para>
/// The references to images and categories are <b>composite</b> foreign keys —
/// <c>(agency_id, asset_id)</c> against <c>(agency_id, id)</c> — rather than plain ones. A plain
/// key would accept another agency's asset id: foreign-key checks run as the table owner and skip
/// row-level security. Pairing the id with the agency makes the database itself refuse a product
/// that shows another agency's photo.
/// </para>
/// <para>
/// The child tables cascade from the product because they are part of it and are replaced when it
/// is saved. Products themselves are never deleted — the application role has no DELETE on them.
/// </para>
/// </remarks>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("products", CatalogSchema.Name);
        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id).ValueGeneratedNever();

        // Stored by name, not number, so a psql query reads "Published" rather than "2" — and so
        // reordering an enum can never silently change what a stored product is.
        builder.Property(product => product.ProductType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(product => product.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(product => product.Title).HasMaxLength(CatalogLimits.MaxTitleLength).IsRequired();
        builder.Property(product => product.Slug).HasMaxLength(ProductSlug.MaxLength).IsRequired();
        builder.Property(product => product.Summary).HasMaxLength(CatalogLimits.MaxSummaryLength).IsRequired();
        builder.Property(product => product.Description).HasMaxLength(CatalogLimits.MaxDescriptionLength).IsRequired();
        builder.Property(product => product.DestinationCountry).HasMaxLength(2).IsFixedLength();
        builder.Property(product => product.DestinationCity).HasMaxLength(CatalogLimits.MaxCityLength);
        builder.Property(product => product.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // Optimistic concurrency: two saves of the same version cannot both succeed.
        builder.Property(product => product.Version).IsConcurrencyToken().IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(product => product.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(product => new { product.AgencyId, product.HeroAssetId })
            .HasPrincipalKey(asset => new { asset.AgencyId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_products_assets_hero_asset");

        builder.HasMany(product => product.Media)
            .WithOne()
            .HasForeignKey(media => media.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(product => product.Media).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(product => product.Categories)
            .WithOne()
            .HasForeignKey(link => link.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(product => product.Categories).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(product => product.Itinerary)
            .WithOne()
            .HasForeignKey(day => day.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(product => product.Itinerary).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(product => product.Inclusions)
            .WithOne()
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(product => product.Inclusions).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(product => product.PriceVariants)
            .WithOne()
            .HasForeignKey(variant => variant.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(product => product.PriceVariants).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(product => product.Visa)
            .WithOne()
            .HasForeignKey<VisaDetails>(visa => visa.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique per agency, not globally: two agencies' storefronts can both have /tours/lagos-city.
        builder.HasIndex(product => new { product.AgencyId, product.Slug })
            .IsUnique()
            .HasDatabaseName("ix_products_agency_id_slug");

        // The console's product list, filtered by status.
        builder.HasIndex(product => new { product.AgencyId, product.Status })
            .HasDatabaseName("ix_products_agency_id_status");
    }
}

/// <summary><c>catalog.product_media</c>: a product's gallery, in order.</summary>
public sealed class ProductMediaConfiguration : IEntityTypeConfiguration<ProductMedia>
{
    public void Configure(EntityTypeBuilder<ProductMedia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_media", CatalogSchema.Name);
        builder.HasKey(media => media.Id);
        builder.Property(media => media.Id).ValueGeneratedNever();
        builder.Property(media => media.Caption).HasMaxLength(CatalogLimits.MaxCaptionLength);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(media => media.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite, so only the agency's own asset can be attached — see ProductConfiguration.
        // Restrict, so an asset in a gallery can never be deleted from under it.
        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(media => new { media.AgencyId, media.AssetId })
            .HasPrincipalKey(asset => new { asset.AgencyId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(media => new { media.ProductId, media.AssetId })
            .IsUnique()
            .HasDatabaseName("ix_product_media_product_id_asset_id");
    }
}

/// <summary><c>catalog.product_categories</c>: an agency's own categories and themes.</summary>
public sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_categories", CatalogSchema.Name);
        builder.HasKey(category => category.Id);
        builder.Property(category => category.Id).ValueGeneratedNever();

        // citext, so "Beach" and "beach" are the same category and the unique index below says so.
        builder.Property(category => category.Name)
            .HasColumnType("citext")
            .HasMaxLength(ProductCategory.MaxNameLength)
            .IsRequired();
        builder.Property(category => category.Type).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(category => category.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(category => new { category.AgencyId, category.Type, category.Name })
            .IsUnique()
            .HasDatabaseName("ix_product_categories_agency_id_type_name");
    }
}

/// <summary><c>catalog.product_category_map</c>: which products carry which categories.</summary>
public sealed class ProductCategoryLinkConfiguration : IEntityTypeConfiguration<ProductCategoryLink>
{
    public void Configure(EntityTypeBuilder<ProductCategoryLink> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_category_map", CatalogSchema.Name);
        builder.HasKey(link => link.Id);
        builder.Property(link => link.Id).ValueGeneratedNever();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(link => link.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite, so a product can only be tagged with its own agency's category. Restrict, so a
        // category in use cannot be deleted and silently empty a storefront filter.
        builder.HasOne<ProductCategory>()
            .WithMany()
            .HasForeignKey(link => new { link.AgencyId, link.CategoryId })
            .HasPrincipalKey(category => new { category.AgencyId, category.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.ProductId, link.CategoryId })
            .IsUnique()
            .HasDatabaseName("ix_product_category_map_product_id_category_id");
    }
}

/// <summary><c>catalog.tour_itinerary_days</c>: day 1, day 2… of a tour or package.</summary>
public sealed class TourItineraryDayConfiguration : IEntityTypeConfiguration<TourItineraryDay>
{
    public void Configure(EntityTypeBuilder<TourItineraryDay> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tour_itinerary_days", CatalogSchema.Name);
        builder.HasKey(day => day.Id);
        builder.Property(day => day.Id).ValueGeneratedNever();

        builder.Property(day => day.Title).HasMaxLength(CatalogLimits.MaxDayTitleLength).IsRequired();
        builder.Property(day => day.Description).HasMaxLength(CatalogLimits.MaxDayDescriptionLength).IsRequired();
        builder.Property(day => day.Accommodation).HasMaxLength(CatalogLimits.MaxAccommodationLength);

        // Read from the three meal columns; not a column of its own.
        builder.Ignore(day => day.Meals);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(day => day.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(day => new { day.ProductId, day.DayNumber })
            .IsUnique()
            .HasDatabaseName("ix_tour_itinerary_days_product_id_day_number");

        builder.HasIndex(day => day.AgencyId).HasDatabaseName("ix_tour_itinerary_days_agency_id");
    }
}

/// <summary><c>catalog.product_inclusions</c>: what the price includes, and what it does not.</summary>
public sealed class ProductInclusionConfiguration : IEntityTypeConfiguration<ProductInclusion>
{
    public void Configure(EntityTypeBuilder<ProductInclusion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_inclusions", CatalogSchema.Name);
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();

        builder.Property(line => line.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(line => line.Text).HasMaxLength(CatalogLimits.MaxInclusionTextLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(line => line.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(line => line.AgencyId).HasDatabaseName("ix_product_inclusions_agency_id");
    }
}

/// <summary><c>catalog.product_price_variants</c>: prices by traveller, room and group size.</summary>
public sealed class ProductPriceVariantConfiguration : IEntityTypeConfiguration<ProductPriceVariant>
{
    public void Configure(EntityTypeBuilder<ProductPriceVariant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_price_variants", CatalogSchema.Name);
        builder.HasKey(variant => variant.Id);
        builder.Property(variant => variant.Id).ValueGeneratedNever();

        builder.Property(variant => variant.Name).HasMaxLength(CatalogLimits.MaxVariantNameLength).IsRequired();
        builder.Property(variant => variant.PaxType).HasConversion<string>().HasMaxLength(10).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(variant => variant.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(variant => variant.AgencyId).HasDatabaseName("ix_product_price_variants_agency_id");
    }
}

/// <summary><c>catalog.visa_details</c>: one row per visa product.</summary>
public sealed class VisaDetailsConfiguration : IEntityTypeConfiguration<VisaDetails>
{
    public void Configure(EntityTypeBuilder<VisaDetails> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("visa_details", CatalogSchema.Name);
        builder.HasKey(visa => visa.Id);
        builder.Property(visa => visa.Id).ValueGeneratedNever();

        builder.Property(visa => visa.VisaType).HasMaxLength(CatalogLimits.MaxVisaTypeLength).IsRequired();
        builder.Property(visa => visa.EntryType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(visa => visa.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(visa => visa.Documents)
            .WithOne()
            .HasForeignKey(document => document.VisaDetailsId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(visa => visa.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(visa => visa.AgencyId).HasDatabaseName("ix_visa_details_agency_id");
    }
}

/// <summary><c>catalog.visa_document_requirements</c>: the applicant's checklist, in order.</summary>
public sealed class VisaDocumentRequirementConfiguration : IEntityTypeConfiguration<VisaDocumentRequirement>
{
    public void Configure(EntityTypeBuilder<VisaDocumentRequirement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("visa_document_requirements", CatalogSchema.Name);
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();

        builder.Property(document => document.Label).HasMaxLength(CatalogLimits.MaxDocumentLabelLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(document => document.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(document => document.AgencyId).HasDatabaseName("ix_visa_document_requirements_agency_id");
    }
}
