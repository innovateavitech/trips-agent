using FluentAssertions;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// One spelling per hostname, and the denylist that stops squatting (open question 20). A hostname that
/// slips through here would dodge the platform-wide unique index or pass itself off as another.
/// </summary>
public class HostnameRulesTests
{
    [Theory]
    [InlineData("Booking.Zara.COM.", "booking.zara.com")]
    [InlineData("  www.example.com ", "www.example.com")]
    [InlineData("shop.agency.com.ng", "shop.agency.com.ng")]
    public void A_hostname_is_normalised_to_one_spelling(string input, string expected)
    {
        Hostnames.TryNormalise(input, out var hostname, out var problem).Should().BeTrue(problem);
        hostname.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("under_score.example.com")]
    [InlineData("two..dots.com")]
    [InlineData("192.168.0.1")]
    public void Something_that_is_not_a_hostname_is_refused(string input)
    {
        Hostnames.TryNormalise(input, out _, out var problem).Should().BeFalse();
        problem.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_label_longer_than_63_characters_is_refused()
    {
        Hostnames.TryNormalise($"{new string('a', 64)}.example.com", out _, out _).Should().BeFalse();
        Hostnames.TryNormalise($"{new string('a', 63)}.example.com", out _, out _).Should().BeTrue();
    }

    [Fact]
    public void An_international_name_is_stored_as_punycode()
    {
        Hostnames.TryNormalise("café.example.com", out var hostname, out var problem).Should().BeTrue(problem);
        hostname.Should().Be("xn--caf-dma.example.com");
    }

    [Fact]
    public void A_label_that_mixes_alphabets_is_refused()
    {
        // "аpple.com" with a Cyrillic а: to a traveller it is apple.com, to DNS it is another host.
        Hostnames.TryNormalise("xn--pple-43d.com", out _, out var problem).Should().BeFalse();
        problem.Should().Contain("alphabets");
    }

    [Theory]
    [InlineData("Lagos-Travel.localhost:3000", "lagos-travel.localhost")]
    [InlineData("example.com.", "example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("[::1]:5000", null)]
    [InlineData("bad host", null)]
    [InlineData("", null)]
    public void A_host_header_becomes_a_lookup_key_or_nothing(string raw, string? expected) =>
        Hostnames.NormaliseHostHeader(raw).Should().Be(expected);

    [Fact]
    public void An_oversized_host_header_is_refused_before_anything_reads_it() =>
        Hostnames.NormaliseHostHeader(new string('a', 400) + ".com").Should().BeNull();

    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("adm1n")]
    [InlineData("supp0rt")]
    [InlineData("trips")]
    [InlineData("tr1ps-agent")]
    [InlineData("trips-africa")]
    public void A_reserved_word_or_its_lookalike_is_reserved(string label) =>
        ReservedHostnames.IsReserved(label, ReservedHostnames.BaseLabels.Select(entry => entry.Label)).Should().BeTrue();

    [Theory]
    [InlineData("lagos-travel")]
    [InlineData("dream-trips")]
    [InlineData("admin-site")]
    public void An_ordinary_agency_name_is_not_reserved(string label) =>
        ReservedHostnames.IsReserved(label, ReservedHostnames.BaseLabels.Select(entry => entry.Label)).Should().BeFalse();

    [Theory]
    [InlineData("emirates-deals", true)]
    [InlineData("qatarairways-ng", true)]
    [InlineData("air-peace-tickets", true)]
    [InlineData("uba", true)]
    [InlineData("klm", true)]
    [InlineData("dubai-holidays", false)]
    [InlineData("klmtravel", false)]
    [InlineData("lagos-travel", false)]
    public void A_name_that_looks_like_a_known_brand_is_set_aside_for_review(string label, bool looksLikeBrand) =>
        ReservedHostnames.LooksLikeBrand(label, ReservedHostnames.KnownBrands).Should().Be(looksLikeBrand);

    [Fact]
    public void The_free_address_starts_from_the_slug_and_counts_up()
    {
        var label = FreeSubdomains.BaseLabel("lagos-travel");

        FreeSubdomains.Candidates(label).Take(3).Should().Equal("lagos-travel", "lagos-travel-2", "lagos-travel-3");
        FreeSubdomains.MovedAside("admin").Should().Be("admin-site");
        FreeSubdomains.BaseLabel(new string('a', 70)).Length.Should().Be(FreeSubdomains.MaxBaseLength);
    }

    [Fact]
    public void Nothing_in_the_denylists_is_traveller_facing_copy_but_every_entry_is_a_valid_label()
    {
        ReservedHostnames.BaseLabels.Should().OnlyContain(entry => Hostnames.IsValidLabel(entry.Label));
        ReservedHostnames.KnownBrands.Should().OnlyContain(brand => Hostnames.IsValidLabel(brand));
    }
}
