using FluentAssertions;
using QuestPDF.Infrastructure;
using SkiaSharp;
using TripsAgent.Application.Documents;
using TripsAgent.Documents;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;
using UglyToad.PdfPig;

namespace TripsAgent.UnitTests.Documents;

/// <summary>
/// The invoice and voucher templates (#46), read back out of the PDF they produce — what a traveller
/// would actually see, not the model that went in.
/// </summary>
public class DocumentRenderingTests
{
    private static readonly QuestPdfDocumentRenderer Renderer = new(LicenseType.Community);

    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 11, 9, 30, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the invoice

    [Fact]
    public void An_invoice_prints_the_agency_the_customer_the_number_and_the_total_in_the_customers_currency()
    {
        var text = Squash(TextOf(Renderer.Render(Invoice()).Pdf));

        text.Should().Contain("LagosTravel")
            .And.Contain("INV-LAGOST-2026-000042")
            .And.Contain("AdaObi")
            .And.Contain("ORD-2026-000142")
            .And.Contain("NGN142,500.00")
            .And.Contain("Paid")
            .And.Contain("12,MarinaRoad,Lagos")
            .And.Contain("12345678-0001");
    }

    [Fact]
    public void An_invoice_never_shows_the_net_rate_or_the_markup()
    {
        // Only the total is in the model, but the words must not appear either.
        var text = TextOf(Renderer.Render(Invoice()).Pdf);

        text.Should().NotContainEquivalentOf("markup").And.NotContainEquivalentOf("net rate");
    }

    // ------------------------------------------------------------------ the vouchers

    [Fact]
    public void A_flight_voucher_prints_the_pnr_every_flight_and_every_ticket()
    {
        var rendered = Renderer.Render(FlightVoucher());
        var text = Squash(TextOf(rendered.Pdf));

        rendered.TemplateKey.Should().Be(DocumentTemplates.FlightVoucher.Key);
        text.Should().Contain("QX7K2P")
            .And.Contain("P47121")
            .And.Contain("LOS")
            .And.Contain("ABV")
            .And.Contain("Fri2Oct2026,06:45")
            .And.Contain("0741234567890")
            .And.Contain("0741234567891");
    }

    [Fact]
    public void A_bus_ticket_prints_the_operator_the_departure_and_each_seat()
    {
        var rendered = Renderer.Render(BusTicket());
        var text = Squash(TextOf(rendered.Pdf));

        rendered.TemplateKey.Should().Be(DocumentTemplates.BusVoucher.Key);
        text.Should().Contain("GIGMobility").And.Contain("Sat3Oct2026,07:00").And.Contain("14").And.Contain("15");
    }

    [Theory]
    [InlineData(OrderLineItemType.Tour)]
    [InlineData(OrderLineItemType.Visa)]
    [InlineData(OrderLineItemType.GroupDeparture)]
    public void Tours_visas_and_group_departures_share_one_plain_voucher_until_the_catalog_arrives(OrderLineItemType product)
    {
        var rendered = Renderer.Render(FlightVoucher() with
        {
            ProductType = product,
            ProductTitle = "Obudu Mountain Resort, 3 nights",
            SupplierReference = null,
            Segments = [],
        });

        rendered.TemplateKey.Should().Be(DocumentTemplates.PlainVoucher.Key);
        Squash(TextOf(rendered.Pdf)).Should().Contain("ObuduMountainResort,3nights").And.Contain("AdaObi");
    }

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void Nothing_on_a_document_or_in_its_properties_names_the_platform()
    {
        foreach (var model in new[] { Invoice(), FlightVoucher(), BusTicket() })
        {
            using var pdf = PdfDocument.Open(Renderer.Render(model).Pdf);

            var text = string.Join('\n', pdf.GetPages().Select(page => page.Text));
            text.Should().NotContainEquivalentOf("Trips Agent").And.NotContainEquivalentOf("tripsagent");

            pdf.Information.Author.Should().Be("Lagos Travel");
            pdf.Information.Creator.Should().Be("Lagos Travel");
            pdf.Information.Producer.Should().Be("Lagos Travel");
        }
    }

    [Fact]
    public void The_same_model_renders_to_the_same_bytes()
    {
        var first = Renderer.Render(FlightVoucher()).Pdf;
        var second = Renderer.Render(FlightVoucher()).Pdf;

        second.Should().Equal(first, "a render that differs each time could never be checked against the original");
    }

    [Fact]
    public void A_reissue_says_which_document_it_replaces()
    {
        var text = Squash(TextOf(Renderer.Render(Invoice() with
        {
            DocumentNumber = "INV-LAGOST-2026-000057",
            IssueNumber = 2,
            SupersedesDocumentNumber = "INV-LAGOST-2026-000042",
        }).Pdf));

        text.Should().Contain("Issue2").And.Contain("replacesINV-LAGOST-2026-000042");
    }

    [Fact]
    public void A_logo_prints_in_the_header_and_the_name_still_appears_beneath_it()
    {
        var rendered = Renderer.Render(Invoice() with { Brand = Brand() with { Logo = Logo() } });

        rendered.Pdf.Length.Should().BeGreaterThan(Renderer.Render(Invoice()).Pdf.Length, "the logo is embedded");
        Squash(TextOf(rendered.Pdf)).Should().Contain("LagosTravel");
    }

    [Fact]
    public void A_brand_colour_that_is_not_a_colour_falls_back_rather_than_failing_the_document()
    {
        var render = () => Renderer.Render(Invoice() with { Brand = Brand() with { PrimaryColor = "not-a-colour" } });

        render.Should().NotThrow();
    }

    [Theory]
    [InlineData(14_250_000, "NGN", "NGN 142,500.00")]
    [InlineData(5, "ngn", "NGN 0.05")]
    [InlineData(-150, "NGN", "NGN -1.50")]
    [InlineData(1_500, "XOF", "XOF 1,500")]
    public void Money_prints_as_the_currency_code_and_the_amount(long amountMinor, string currency, string expected)
    {
        DocumentMoney.Format(amountMinor, currency).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, LicenseType.Community)]
    [InlineData("", LicenseType.Community)]
    [InlineData("professional", LicenseType.Professional)]
    [InlineData(" Enterprise ", LicenseType.Enterprise)]
    public void The_licence_is_whatever_the_business_configured(string? configured, LicenseType expected)
    {
        DocumentRenderingRegistration.ReadLicense(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("Evaluation")]
    [InlineData("free")]
    public void A_licence_setting_that_is_not_a_paid_or_community_tier_stops_the_worker(string configured)
    {
        var read = () => DocumentRenderingRegistration.ReadLicense(configured);

        read.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Community, Professional or Enterprise");
    }

    // ------------------------------------------------------------------ helpers

    private static DocumentBrand Brand() =>
        new("Lagos Travel", "#0A7E3B", null, "12, Marina Road, Lagos", "12345678-0001");

    private static DocumentRenderModel Invoice() => new()
    {
        DocumentType = DocumentType.Invoice,
        DocumentNumber = "INV-LAGOST-2026-000042",
        IssueNumber = 1,
        IssuedAt = IssuedAt,
        IssuedOn = "11 September 2026",
        OrderNumber = "ORD-2026-000142",
        Currency = "NGN",
        Brand = Brand(),
        RecipientName = "Ada Obi",
        Lines = [new DocumentLineItem("Lagos (LOS) to Abuja (ABV), Air Peace", OrderLineItemType.Flight, 2, 14_250_000)],
        TotalMinor = 14_250_000,
        PaymentStatus = "Paid",
    };

    private static DocumentRenderModel FlightVoucher() => new()
    {
        DocumentType = DocumentType.Voucher,
        DocumentNumber = "VCH-LAGOST-2026-000007",
        IssueNumber = 1,
        IssuedAt = IssuedAt,
        IssuedOn = "11 September 2026",
        OrderNumber = "ORD-2026-000142",
        Currency = "NGN",
        Brand = Brand(),
        RecipientName = "Ada Obi",
        ProductType = OrderLineItemType.Flight,
        ProductTitle = "Lagos (LOS) to Abuja (ABV), Air Peace",
        SupplierReference = "QX7K2P",
        Travellers =
        [
            new DocumentTraveller("Ada Obi", "Adult", "0741234567890", null),
            new DocumentTraveller("Chidi Obi", "Child", "0741234567891", null),
        ],
        Segments = [new DocumentSegment("P4 7121", "LOS", "ABV", "Fri 2 Oct 2026, 06:45", "Fri 2 Oct 2026, 08:00", "Economy · 23kg")],
    };

    private static DocumentRenderModel BusTicket() => FlightVoucher() with
    {
        ProductType = OrderLineItemType.Bus,
        ProductTitle = "Lagos (Jibowu) to Abuja (Utako), GIG Mobility",
        SupplierReference = "GIG7Q4",
        Travellers = [new DocumentTraveller("Ada Obi", "Adult", null, "14"), new DocumentTraveller("Chidi Obi", "Child", null, "15")],
        Segments = [new DocumentSegment("GIG Mobility", null, null, "Sat 3 Oct 2026, 07:00", "Sat 3 Oct 2026, 18:00", null)],
    };

    private static byte[] Logo()
    {
        using var bitmap = new SKBitmap(240, 80);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.DarkGreen);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    /// <summary>The text without whitespace, so an assertion does not depend on how a PDF spaces its words.</summary>
    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
