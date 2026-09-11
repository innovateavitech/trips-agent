using FluentAssertions;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.UnitTests.Notifications;

/// <summary>
/// The renderer's two jobs: fill templates safely, and refuse to put our brand in front of a
/// traveller (CLAUDE.md rule 4).
/// </summary>
public class NotificationRendererTests
{
    private static readonly NotificationBrand Agency = new(
        "Lagos Travel", "#0A7E3B", LogoUrl: null, Contact: "12 Marina, Lagos", ReplyTo: "owner@lagos-travel.test");

    private static readonly Dictionary<string, string> Booking = new(StringComparer.Ordinal)
    {
        ["bookingReference"] = "LT-1001",
        ["itinerarySummary"] = "Lagos (LOS) to Abuja (ABV)",
        ["travellerNames"] = "Ada Obi",
        ["departureDate"] = "3 October 2026",
    };

    // ------------------------------------------------------------------ the brand rule

    [Fact]
    public void Traveller_mail_renders_with_the_agencys_name_and_colour()
    {
        var rendered = Render(NotificationTemplateCatalog.BookingConfirmed, Agency, Booking);

        rendered.Html.Should().Contain("Lagos Travel").And.Contain("#0A7E3B");
        rendered.Text.Should().Contain("Lagos Travel").And.Contain("12 Marina, Lagos");
    }

    [Fact]
    public void Traveller_mail_never_mentions_us()
    {
        var rendered = Render(NotificationTemplateCatalog.BookingConfirmed, Agency, Booking);

        foreach (var part in new[] { rendered.Subject, rendered.Html, rendered.Text })
        {
            part.Should().NotContainEquivalentOf("Trips Agent");
            part.Should().NotContainEquivalentOf("tripsagent");
        }
    }

    [Fact]
    public void A_traveller_template_cannot_render_with_the_Trips_brand()
    {
        var act = () => Render(NotificationTemplateCatalog.BookingConfirmed, NotificationBrand.Platform, Booking);

        act.Should().Throw<NotificationRenderException>().WithMessage("*rule 4*");
    }

    [Fact]
    public void A_traveller_template_cannot_render_with_no_brand_at_all()
    {
        var act = () => Render(NotificationTemplateCatalog.BookingConfirmed, Agency with { Name = " " }, Booking);

        act.Should().Throw<NotificationRenderException>();
    }

    [Fact]
    public void Our_brand_arriving_through_a_variable_is_caught_before_a_traveller_sees_it()
    {
        var leaky = new Dictionary<string, string>(Booking, StringComparer.Ordinal)
        {
            ["itinerarySummary"] = "Booked via tripsagent.com",
        };

        var act = () => Render(NotificationTemplateCatalog.BookingConfirmed, Agency, leaky);

        act.Should().Throw<NotificationRenderException>();
    }

    [Fact]
    public void An_agency_with_Trips_in_its_own_name_can_still_email_its_travellers()
    {
        // Only our product name and domain are refused, not the ordinary word.
        var rendered = Render(NotificationTemplateCatalog.BookingConfirmed, Agency with { Name = "Lagos Trips Ltd" }, Booking);

        rendered.Html.Should().Contain("Lagos Trips Ltd");
    }

    [Fact]
    public void Agency_staff_mail_is_ours_to_brand()
    {
        var rendered = Render(
            NotificationTemplateCatalog.KybApproved,
            NotificationBrand.Platform,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["businessName"] = "Lagos Travel" });

        rendered.Text.Should().Contain(NotificationTemplateCatalog.ProductName);
    }

    // ------------------------------------------------------------------ the logo block

    [Fact]
    public void With_no_logo_the_header_shows_the_name_and_no_empty_image()
    {
        var rendered = Render(NotificationTemplateCatalog.BookingConfirmed, Agency, Booking);

        rendered.Html.Should().NotContain("<img");
        rendered.Html.Should().NotContain(NotificationRenderer.LogoBlockOpen);
    }

    [Fact]
    public void With_a_logo_the_header_shows_the_logo_instead_of_the_name()
    {
        var rendered = Render(
            NotificationTemplateCatalog.BookingConfirmed,
            Agency with { LogoUrl = "https://cdn.example/lagos.png" },
            Booking);

        rendered.Html.Should().Contain("<img src=\"https://cdn.example/lagos.png\"");
        rendered.Html.Should().NotContain(NotificationRenderer.NameBlockOpen);
    }

    [Fact]
    public void A_logo_that_is_not_https_is_not_rendered()
    {
        var rendered = Render(
            NotificationTemplateCatalog.BookingConfirmed,
            Agency with { LogoUrl = "javascript:alert(1)" },
            Booking);

        rendered.Html.Should().NotContain("<img");
    }

    [Fact]
    public void A_colour_that_is_not_hex_falls_back_rather_than_reaching_the_style_attribute()
    {
        var rendered = Render(
            NotificationTemplateCatalog.BookingConfirmed,
            Agency with { Color = "red;background:url(https://evil.example)" },
            Booking);

        rendered.Html.Should().NotContain("evil.example");
        rendered.Html.Should().Contain(AgencyBranding.DefaultPrimaryColor);
    }

    // ------------------------------------------------------------------ filling

    [Fact]
    public void Values_are_HTML_encoded_in_the_HTML_body_and_left_alone_in_the_text()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["businessName"] = "Acme",
            ["reason"] = "<a href=\"https://evil.example\">click</a>",
        };

        var rendered = Render(NotificationTemplateCatalog.KybRejected, NotificationBrand.Platform, values);

        rendered.Html.Should().NotContain("<a href=\"https://evil.example\"").And.Contain("&lt;a href=");
        rendered.Text.Should().Contain("<a href=\"https://evil.example\">click</a>");
    }

    [Fact]
    public void A_line_break_cannot_reach_the_subject_header()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["businessName"] = "Acme\r\nBcc: everyone@example.com",
        };

        var rendered = Render(NotificationTemplateCatalog.KybApproved, NotificationBrand.Platform, values);

        rendered.Subject.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void A_missing_variable_fails_loudly_rather_than_sending_braces()
    {
        var act = () => Render(
            NotificationTemplateCatalog.KybRejected,
            NotificationBrand.Platform,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["businessName"] = "Acme" });

        act.Should().Throw<NotificationRenderException>().WithMessage("*reason*");
    }

    private static RenderedNotification Render(string key, NotificationBrand brand, Dictionary<string, string> values) =>
        NotificationRenderer.Render(
            NotificationTemplateCatalog.Find(key, NotificationChannel.Email)!.ToTemplate(),
            brand,
            "Ada",
            values);
}
