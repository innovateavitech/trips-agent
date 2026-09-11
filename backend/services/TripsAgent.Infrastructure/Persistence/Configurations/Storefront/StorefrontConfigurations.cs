using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Storefront;

/// <summary>Shared constants for the website tables.</summary>
public static class StorefrontSchema
{
    /// <summary>The PostgreSQL schema holding sites, their versions, pages, blocks and hostnames.</summary>
    public const string Name = "storefront";
}

/// <summary>
/// <c>storefront.site_templates</c>. Platform reference data: no agency_id, readable by every agency,
/// writable by none — the migration grants the application role SELECT only.
/// </summary>
public sealed class SiteTemplateConfiguration : IEntityTypeConfiguration<SiteTemplate>
{
    public void Configure(EntityTypeBuilder<SiteTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_templates", StorefrontSchema.Name);
        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id).ValueGeneratedNever();

        builder.Property(template => template.Code).HasMaxLength(SiteTemplate.MaxCodeLength).IsRequired();
        builder.Property(template => template.Name).HasMaxLength(80).IsRequired();
        builder.Property(template => template.Description).HasMaxLength(300).IsRequired();
        builder.Property(template => template.PreviewImageUrl).HasMaxLength(500);
        builder.Property(template => template.BlockSchema).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(template => template.Code).IsUnique().HasDatabaseName("ix_site_templates_code");
    }
}

/// <summary><c>storefront.reserved_hostname_labels</c>. Reference data, like the templates.</summary>
public sealed class ReservedHostnameLabelConfiguration : IEntityTypeConfiguration<ReservedHostnameLabel>
{
    public void Configure(EntityTypeBuilder<ReservedHostnameLabel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("reserved_hostname_labels", StorefrontSchema.Name);
        builder.HasKey(label => label.Id);
        builder.Property(label => label.Id).ValueGeneratedNever();

        builder.Property(label => label.Label).HasMaxLength(63).IsRequired();
        builder.Property(label => label.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(label => label.Reason).HasMaxLength(200).IsRequired();

        builder.HasIndex(label => label.Label).IsUnique().HasDatabaseName("ix_reserved_hostname_labels_label");
    }
}

/// <summary>
/// <c>storefront.sites</c>. One per agency. The three version and domain pointers are held to this
/// site's own rows by composite foreign keys the migration writes by hand, deferred to commit so a site
/// and its first draft can be inserted together.
/// </summary>
public sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sites", StorefrontSchema.Name);
        builder.HasKey(site => site.Id);
        builder.Property(site => site.Id).ValueGeneratedNever();

        builder.Property(site => site.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(site => site.Name).HasMaxLength(Site.MaxNameLength).IsRequired();
        builder.Property(site => site.SeoTitle).HasMaxLength(Site.MaxSeoTitleLength);
        builder.Property(site => site.SeoDescription).HasMaxLength(Site.MaxSeoDescriptionLength);
        builder.Property(site => site.Language).HasMaxLength(10).IsRequired();
        builder.Property(site => site.AnalyticsIds).HasColumnType("jsonb").IsRequired();

        // Publish, rollback and settings changes all bump it, so two of them racing cannot both win.
        builder.Property(site => site.Version).IsConcurrencyToken();

        builder.HasOne<Agency>()
            .WithOne()
            .HasForeignKey<Site>(site => site.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SiteTemplate>()
            .WithMany()
            .HasForeignKey(site => site.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        // One site per agency (plan §2.4). Sub-agents sell under their principal's (open question 6).
        builder.HasIndex(site => site.AgencyId).IsUnique().HasDatabaseName("ix_sites_agency_id");
    }
}

/// <summary>
/// <c>storefront.site_versions</c>. The trigger that freezes every version but the draft is hand-written
/// in the migration.
/// </summary>
public sealed class SiteVersionConfiguration : IEntityTypeConfiguration<SiteVersion>
{
    public void Configure(EntityTypeBuilder<SiteVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_versions", StorefrontSchema.Name);
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id).ValueGeneratedNever();

        builder.Property(version => version.VersionNumber).HasColumnName("version_no");
        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(version => version.ContentSnapshot).HasColumnType("jsonb");
        builder.Property(version => version.ThemeSnapshot).HasColumnType("jsonb");

        builder.Ignore(version => version.CanBeRolledBackTo);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(version => version.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(version => version.SiteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(version => new { version.AgencyId, version.SiteId })
            .HasDatabaseName("ix_site_versions_agency_id_site_id");

        builder.HasIndex(version => new { version.SiteId, version.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ix_site_versions_site_id_version_no");

        // One draft per site. At most one staged and at most one live version are held too, by
        // deferrable exclusion constraints in the migration: a publish archives one version and puts
        // another live in the same save, and a plain unique index would trip over the moment between.
        builder.HasIndex(version => version.SiteId, "ix_site_versions_one_draft_per_site")
            .IsUnique()
            .HasFilter("status = 'Draft'");
    }
}

/// <summary><c>storefront.site_pages</c>. Only the draft has pages; staging copies them into a snapshot.</summary>
public sealed class SitePageConfiguration : IEntityTypeConfiguration<SitePage>
{
    public void Configure(EntityTypeBuilder<SitePage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_pages", StorefrontSchema.Name);
        builder.HasKey(page => page.Id);
        builder.Property(page => page.Id).ValueGeneratedNever();

        builder.Property(page => page.Slug).HasMaxLength(SitePageRules.MaxSlugLength).IsRequired();
        builder.Property(page => page.PageType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(page => page.Title).HasMaxLength(SitePageRules.MaxTitleLength).IsRequired();
        builder.Property(page => page.MetaTitle).HasMaxLength(SitePageRules.MaxMetaTitleLength);
        builder.Property(page => page.MetaDescription).HasMaxLength(SitePageRules.MaxMetaDescriptionLength);

        // Two browser tabs saving the same page: the second finds the revision moved and gets a 409.
        builder.Property(page => page.Revision).IsConcurrencyToken();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(page => page.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SiteVersion>()
            .WithMany()
            .HasForeignKey(page => page.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(page => page.Blocks)
            .WithOne()
            .HasForeignKey(block => block.PageId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(page => page.Blocks).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(page => new { page.AgencyId, page.VersionId })
            .HasDatabaseName("ix_site_pages_agency_id_version_id");

        builder.HasIndex(page => new { page.VersionId, page.Slug })
            .IsUnique()
            .HasDatabaseName("ix_site_pages_version_id_slug");

        // One home, one about, one contact… per version. Custom pages are as many as the agent wants.
        builder.HasIndex(page => new { page.VersionId, page.PageType }, "ix_site_pages_one_system_page_per_type")
            .IsUnique()
            .HasFilter("page_type <> 'Custom'");
    }
}

/// <summary>
/// <c>storefront.site_blocks</c>. (page_id, position) is unique through a deferrable constraint in the
/// migration, so a reorder can pass through a moment of duplicates inside its transaction.
/// </summary>
public sealed class SiteBlockConfiguration : IEntityTypeConfiguration<SiteBlock>
{
    public void Configure(EntityTypeBuilder<SiteBlock> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_blocks", StorefrontSchema.Name);
        builder.HasKey(block => block.Id);
        builder.Property(block => block.Id).ValueGeneratedNever();

        builder.Property(block => block.BlockType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(block => block.Config).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(block => block.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(block => new { block.AgencyId, block.PageId })
            .HasDatabaseName("ix_site_blocks_agency_id_page_id");
    }
}

/// <summary><c>storefront.site_themes</c>. One per site; typography only — the logo and colours are branding's.</summary>
public sealed class SiteThemeConfiguration : IEntityTypeConfiguration<SiteTheme>
{
    public void Configure(EntityTypeBuilder<SiteTheme> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_themes", StorefrontSchema.Name);
        builder.HasKey(theme => theme.Id);
        builder.Property(theme => theme.Id).ValueGeneratedNever();

        builder.Property(theme => theme.Typography).HasColumnType("jsonb").IsRequired();
        builder.Property(theme => theme.Colors).HasColumnType("jsonb").IsRequired();
        builder.Property(theme => theme.CustomCss);

        builder.Ignore(theme => theme.HeadingFont);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(theme => theme.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Site>()
            .WithOne()
            .HasForeignKey<SiteTheme>(theme => theme.SiteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(theme => theme.AgencyId).HasDatabaseName("ix_site_themes_agency_id");
        builder.HasIndex(theme => theme.SiteId).IsUnique().HasDatabaseName("ix_site_themes_site_id");
    }
}

/// <summary>
/// <c>storefront.site_domains</c>. <c>hostname</c> is citext and globally unique — not unique per agency,
/// because two agencies claiming one hostname is exactly what it must prevent.
/// </summary>
public sealed class SiteDomainConfiguration : IEntityTypeConfiguration<SiteDomain>
{
    public void Configure(EntityTypeBuilder<SiteDomain> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_domains", StorefrontSchema.Name);
        builder.HasKey(domain => domain.Id);
        builder.Property(domain => domain.Id).ValueGeneratedNever();

        builder.Property(domain => domain.Hostname).HasColumnType("citext").IsRequired();
        builder.Property(domain => domain.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(domain => domain.VerificationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(domain => domain.VerificationToken).HasMaxLength(100);
        builder.Property(domain => domain.SslStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(domain => domain.SslLastError).HasMaxLength(500);

        builder.Ignore(domain => domain.IsVerified);
        builder.Ignore(domain => domain.CanServe);
        builder.Ignore(domain => domain.CanServeSecurely);
        builder.Ignore(domain => domain.TxtRecordName);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(domain => domain.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(domain => domain.SiteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(domain => domain.Hostname).IsUnique().HasDatabaseName("ix_site_domains_hostname");

        builder.HasIndex(domain => new { domain.AgencyId, domain.SiteId })
            .HasDatabaseName("ix_site_domains_agency_id_site_id");

        // What the verification and certificate jobs sweep.
        builder.HasIndex(domain => new { domain.VerificationStatus, domain.NextCheckAt })
            .HasDatabaseName("ix_site_domains_verification_status_next_check_at");

        builder.HasIndex(domain => new { domain.SslStatus, domain.SslNextAttemptAt })
            .HasDatabaseName("ix_site_domains_ssl_status_ssl_next_attempt_at");

        // Every site has exactly one free address.
        builder.HasIndex(domain => domain.SiteId, "ix_site_domains_one_subdomain_per_site")
            .IsUnique()
            .HasFilter("type = 'Subdomain'");
    }
}

/// <summary><c>storefront.site_domain_checks</c>. Append-only: the application role may insert and read.</summary>
public sealed class SiteDomainCheckConfiguration : IEntityTypeConfiguration<SiteDomainCheck>
{
    public void Configure(EntityTypeBuilder<SiteDomainCheck> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("site_domain_checks", StorefrontSchema.Name);
        builder.HasKey(check => check.Id);
        builder.Property(check => check.Id).ValueGeneratedNever();

        builder.Property(check => check.Kind).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(check => check.RecordName).HasMaxLength(SiteDomainCheck.MaxValueLength).IsRequired();
        builder.Property(check => check.ExpectedValue).HasMaxLength(SiteDomainCheck.MaxValueLength).IsRequired();
        builder.Property(check => check.ObservedValues).HasColumnType("text[]").IsRequired();
        builder.Property(check => check.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(check => check.Resolver).HasMaxLength(SiteDomainCheck.MaxValueLength).IsRequired();
        builder.Property(check => check.ErrorDetail).HasMaxLength(SiteDomainCheck.MaxValueLength);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(check => check.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SiteDomain>()
            .WithMany()
            .HasForeignKey(check => check.SiteDomainId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(check => new { check.AgencyId, check.SiteDomainId })
            .HasDatabaseName("ix_site_domain_checks_agency_id_site_domain_id");

        // A host's history, newest first — how the console always reads it.
        builder.HasIndex(check => new { check.SiteDomainId, check.CheckedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_site_domain_checks_site_domain_id_checked_at");
    }
}
