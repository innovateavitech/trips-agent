using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Pricing;

/// <summary>Shared constants for the pricing tables.</summary>
public static class PricingSchema
{
    /// <summary>The PostgreSQL schema holding markup rules and price quotes.</summary>
    public const string Name = "pricing";
}

/// <summary>
/// <c>pricing.markup_rules</c>. The shape rules and the terms-never-change trigger are in the
/// migration, as hand-written SQL: EF Core has no way to express either.
/// </summary>
public sealed class MarkupRuleConfiguration : IEntityTypeConfiguration<MarkupRule>
{
    public void Configure(EntityTypeBuilder<MarkupRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("markup_rules", PricingSchema.Name);
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id).ValueGeneratedNever();

        // Stored by name, not number, so a psql query reads "Product" rather than "4" — and so
        // reordering the enum can never silently change what a stored rule means.
        builder.Property(rule => rule.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(rule => rule.ProductType).HasConversion<string>().HasMaxLength(30);
        builder.Property(rule => rule.CalculationType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(rule => rule.SupplierCode).HasMaxLength(MarkupRuleTerms.MaxSupplierCodeLength);
        builder.Property(rule => rule.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // Terms is a computed view over the columns, not a column of its own.
        builder.Ignore(rule => rule.Terms);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(rule => rule.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MarkupRule>()
            .WithMany()
            .HasForeignKey(rule => rule.SupersededById)
            .OnDelete(DeleteBehavior.Restrict);

        // What the pricing cache loads on a miss: one agency's rules that have not ended.
        builder.HasIndex(rule => new { rule.AgencyId, rule.EffectiveTo })
            .HasDatabaseName("ix_markup_rules_agency_id_effective_to");
    }
}

/// <summary><c>pricing.price_quotes</c>: a price as it was worked out, with the rule that decided it.</summary>
public sealed class PriceQuoteConfiguration : IEntityTypeConfiguration<PriceQuote>
{
    public void Configure(EntityTypeBuilder<PriceQuote> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("price_quotes", PricingSchema.Name);
        builder.HasKey(quote => quote.Id);
        builder.Property(quote => quote.Id).ValueGeneratedNever();

        builder.Property(quote => quote.ProductType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(quote => quote.SupplierCode).HasMaxLength(MarkupRuleTerms.MaxSupplierCodeLength);
        builder.Property(quote => quote.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // The rate is stored as FxRateBillionths, a bigint; this is the same value read as a decimal.
        builder.Ignore(quote => quote.FxRate);

        // jsonb, so the calculation can be queried in psql — "every quote priced by this rule at
        // more than 15%" — without a schema change for each question.
        builder.Property(quote => quote.Breakdown).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(quote => quote.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, so a rule that has priced anything can never be deleted out from under the
        // quotes that name it. (The trigger refuses every delete anyway; this says so in the schema.)
        builder.HasOne<MarkupRule>()
            .WithMany()
            .HasForeignKey(quote => quote.MarkupRuleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(quote => new { quote.AgencyId, quote.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_price_quotes_agency_id_created_at");

        // "Which prices did this rule produce?" — the question a margin report asks of a rule.
        builder.HasIndex(quote => quote.MarkupRuleId)
            .HasDatabaseName("ix_price_quotes_markup_rule_id");
    }
}
