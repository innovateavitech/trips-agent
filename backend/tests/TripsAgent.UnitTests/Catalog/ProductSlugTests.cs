using FluentAssertions;
using TripsAgent.Domain.Catalog;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>Storefront addresses made from titles, and kept unique within an agency.</summary>
public class ProductSlugTests
{
    [Theory]
    [InlineData("Zanzibar Escape", "zanzibar-escape")]
    [InlineData("  Zanzibar   Escape — 5 Days!  ", "zanzibar-escape-5-days")]
    [InlineData("Café à Lagos", "cafe-a-lagos")]
    [InlineData("Lagos's Best", "lagoss-best")]
    [InlineData("Sun & Sand", "sun-and-sand")]
    [InlineData("ÉTÉ À ZÜRICH", "ete-a-zurich")]
    [InlineData("!!!", "")]
    [InlineData("", "")]
    public void Free_text_becomes_lower_case_words_joined_by_hyphens(string text, string slug)
    {
        ProductSlug.Normalise(text).Should().Be(slug);
    }

    [Fact]
    public void A_long_title_is_cut_to_the_limit_without_a_dangling_hyphen()
    {
        var slug = ProductSlug.Normalise(string.Join(' ', Enumerable.Repeat("safari", 40)));

        slug.Length.Should().BeLessThanOrEqualTo(ProductSlug.MaxLength);
        slug.Should().NotEndWith("-");
        ProductSlug.IsValid(slug).Should().BeTrue();
    }

    [Fact]
    public void A_title_with_nothing_usable_in_it_is_untitled()
    {
        ProductSlug.FromTitle("   ").Should().Be("untitled");
        ProductSlug.FromTitle("???").Should().Be("untitled");
    }

    [Fact]
    public void The_first_free_slug_is_the_base_or_the_next_number_up()
    {
        ProductSlug.FirstFree("zanzibar", new HashSet<string>()).Should().Be("zanzibar");
        ProductSlug.FirstFree("zanzibar", new HashSet<string> { "zanzibar" }).Should().Be("zanzibar-2");
        ProductSlug.FirstFree("zanzibar", new HashSet<string> { "zanzibar", "zanzibar-2", "zanzibar-3" })
            .Should().Be("zanzibar-4");
    }

    [Fact]
    public void A_numbered_slug_still_fits_the_column()
    {
        var longest = new string('a', ProductSlug.MaxLength);

        var numbered = ProductSlug.WithSuffix(longest, 12);

        numbered.Should().HaveLength(ProductSlug.MaxLength).And.EndWith("-12");
        ProductSlug.IsValid(numbered).Should().BeTrue();
    }

    [Theory]
    [InlineData("zanzibar-escape", "zanzibar-escape", true)]
    [InlineData("zanzibar-escape-2", "zanzibar-escape", true)]
    [InlineData("zanzibar-escape-17", "zanzibar-escape", true)]
    [InlineData("zanzibar-escape-tours", "zanzibar-escape", false)]
    [InlineData("zanzibar", "zanzibar-escape", false)]
    public void A_numbered_slug_belongs_to_its_base(string slug, string baseSlug, bool belongs)
    {
        ProductSlug.BelongsTo(slug, baseSlug).Should().Be(belongs);
    }

    [Theory]
    [InlineData("zanzibar-escape", true)]
    [InlineData("tour-2026", true)]
    [InlineData("Zanzibar", false)]
    [InlineData("zanzibar--escape", false)]
    [InlineData("-zanzibar", false)]
    [InlineData("zanzibar_escape", false)]
    [InlineData("", false)]
    public void A_slug_is_lower_case_letters_digits_and_single_hyphens(string slug, bool valid)
    {
        ProductSlug.IsValid(slug).Should().Be(valid);
    }
}
