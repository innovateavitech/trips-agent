using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Documents;

/// <summary>
/// The invoice: who it is billed to, what was bought, and what was paid, in the customer's currency.
/// </summary>
/// <remarks>
/// Each line shows what the traveller paid for it, tax included — never the net rate or the markup,
/// and not the tax either, which at a known VAT rate would give both away (open question 25).
/// </remarks>
internal sealed class InvoiceDocument(DocumentRenderModel model) : IDocument
{
    public void Compose(IDocumentContainer container) =>
        BrandedPage.Compose(container, model, "INVOICE", Content);

    private void Content(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(4);

            column.Item().Row(row =>
            {
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Billed to", model.RecipientName, 12));
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Booking reference", model.OrderNumber));
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Date issued", model.IssuedOn));
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Status", model.PaymentStatus));
            });

            if (!string.IsNullOrWhiteSpace(model.Brand.TaxId))
            {
                column.Item().PaddingTop(6).Text($"Tax identification number: {model.Brand.TaxId}").FontSize(9).FontColor(BrandedPage.Muted);
            }

            column.Item().Element(cell => BrandedPage.SectionHeading(cell, "What you bought"));
            column.Item().Element(Lines);

            column.Item().PaddingTop(10).AlignRight().Row(row =>
            {
                row.AutoItem().PaddingRight(16).AlignMiddle().Text("Total paid").FontSize(11).SemiBold();
                row.AutoItem().Text(DocumentMoney.Format(model.TotalMinor, model.Currency)).FontSize(14).Bold();
            });

            column.Item().PaddingTop(16).Text(
                    $"Prices include all applicable taxes. Amounts are in {model.Currency.ToUpperInvariant()}.")
                .FontSize(9).FontColor(BrandedPage.Muted);

            if (model.IssueNumber > 1 && model.SupersedesDocumentNumber is { } replaced)
            {
                column.Item().Text($"This invoice replaces {replaced}, which should no longer be used.")
                    .FontSize(9).FontColor(BrandedPage.Muted);
            }
        });
    }

    private void Lines(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(24);
                columns.RelativeColumn(6);
                columns.RelativeColumn(2);
                columns.RelativeColumn(3);
            });

            table.Header(header =>
            {
                header.Cell().Element(BrandedPage.HeaderCell).Text("#").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Description").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).AlignRight().Text("Travellers").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).AlignRight().Text("Amount").SemiBold();
            });

            var number = 0;

            foreach (var line in model.Lines)
            {
                number++;

                table.Cell().Element(BrandedPage.BodyCell).Text(number.ToString(CultureInfo.InvariantCulture));
                table.Cell().Element(BrandedPage.BodyCell).Column(description =>
                {
                    description.Item().Text(line.Description);
                    description.Item().Text(ProductLabel(line.ProductType)).FontSize(8).FontColor(BrandedPage.Muted);
                });
                table.Cell().Element(BrandedPage.BodyCell).AlignRight()
                    .Text(line.Travellers.ToString(CultureInfo.InvariantCulture));
                table.Cell().Element(BrandedPage.BodyCell).AlignRight()
                    .Text(DocumentMoney.Format(line.AmountMinor, model.Currency));
            }
        });
    }

    private static string ProductLabel(OrderLineItemType productType) => productType switch
    {
        OrderLineItemType.Flight => "Flight",
        OrderLineItemType.Bus => "Bus",
        OrderLineItemType.Tour => "Tour",
        OrderLineItemType.Visa => "Visa",
        OrderLineItemType.GroupDeparture => "Group departure",
        _ => productType.ToString(),
    };
}
