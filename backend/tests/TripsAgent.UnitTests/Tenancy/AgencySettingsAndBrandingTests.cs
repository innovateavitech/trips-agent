using FluentAssertions;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.UnitTests.Tenancy;

public class AgencySettingsAndBrandingTests
{
    private static Agency NewAgency(string slug = "lagos-travel") =>
        Agency.RegisterPrincipal("Lagos Travel Limited", slug, "NG", "NGN", "Africa/Lagos");

    // ----------------------------------------------------------------------------- settings

    [Fact]
    public void Default_settings_belong_to_the_agency_and_quote_its_base_currency()
    {
        var agency = NewAgency();

        var settings = AgencySettings.CreateDefault(agency);

        settings.AgencyId.Should().Be(agency.Id);
        settings.SupportedCurrencies.Should().Equal("NGN");
    }

    [Theory]
    [InlineData("lagos-travel", "LAGOST", "INV-LAGOST")]
    [InlineData("abc", "ABC", "INV-ABC")]
    [InlineData("a-b-c-d-e-f-g", "ABCDEF", "INV-ABCDEF")]
    public void Prefixes_are_derived_from_the_slug(string slug, string expectedBooking, string expectedInvoice)
    {
        // Booking references and invoice numbers are quoted back to us by travellers and
        // accountants, so they should be readable and obviously belong to this agency.
        var settings = AgencySettings.CreateDefault(NewAgency(slug));

        settings.BookingReferencePrefix.Should().Be(expectedBooking);
        settings.InvoicePrefix.Should().Be(expectedInvoice);
    }

    [Fact]
    public void The_base_currency_survives_a_currency_list_that_omits_it()
    {
        var agency = NewAgency();
        var settings = AgencySettings.CreateDefault(agency);

        settings.SetSupportedCurrencies(["usd", "gbp"], agency.BaseCurrency);

        // Dropping your own base currency would leave the books unquotable.
        settings.SupportedCurrencies.Should().Contain("NGN");
        settings.SupportedCurrencies.Should().Equal("GBP", "NGN", "USD");
    }

    [Fact]
    public void Duplicate_and_blank_currencies_are_discarded()
    {
        var agency = NewAgency();
        var settings = AgencySettings.CreateDefault(agency);

        settings.SetSupportedCurrencies(["usd", "USD", "  ", "usd"], agency.BaseCurrency);

        settings.SupportedCurrencies.Should().Equal("NGN", "USD");
    }

    // ----------------------------------------------------------------------------- branding

    [Fact]
    public void Default_branding_is_neutral_rather_than_the_Trips_brand()
    {
        var branding = AgencyBranding.CreateDefault(NewAgency());

        // Nothing traveller-facing may carry our brand — CLAUDE.md rule 4. A new agency that has
        // not picked colours yet gets a neutral dark, not Trips blue.
        branding.PrimaryColor.Should().Be("#1F2933");
        branding.PrimaryColor.Should().NotBe("#325DEC", "that is the Trips brand colour");
        branding.LogoAssetId.Should().BeNull();
    }

    [Theory]
    [InlineData("#325dec", "#325DEC")]
    [InlineData("#ABC", "#ABC")]
    [InlineData("  #325DEC  ", "#325DEC")]
    public void Hex_colours_are_normalised(string given, string expected)
    {
        var branding = AgencyBranding.CreateDefault(NewAgency());

        branding.SetColors(given, null);

        branding.PrimaryColor.Should().Be(expected);
    }

    [Theory]
    [InlineData("325DEC")]      // no hash
    [InlineData("#GGGGGG")]     // not hex
    [InlineData("#12345")]      // wrong length
    [InlineData("rebeccapurple")]
    public void A_malformed_colour_is_rejected(string colour)
    {
        var branding = AgencyBranding.CreateDefault(NewAgency());

        var act = () => branding.SetColors(colour, null);

        // A bad colour renders an agency's whole storefront wrong and is tedious to trace back
        // to its source, so it is caught at the point of assignment.
        act.Should().Throw<ArgumentException>().WithMessage("*not a hex colour*");
    }

    [Fact]
    public void A_blank_secondary_colour_becomes_null()
    {
        var branding = AgencyBranding.CreateDefault(NewAgency());

        branding.SetColors("#325DEC", "   ");

        branding.SecondaryColor.Should().BeNull();
    }

    [Fact]
    public void A_blank_contact_address_becomes_null()
    {
        var branding = AgencyBranding.CreateDefault(NewAgency());

        branding.SetContactAddress("   ");

        branding.ContactAddress.Should().BeNull();
    }
}
