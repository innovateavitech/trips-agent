using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Documents;

/// <summary>
/// The voucher: what the traveller shows at check-in, at the terminal, or to the guide. One layout,
/// with the middle drawn for the product it is for.
/// </summary>
internal sealed class VoucherDocument(DocumentRenderModel model) : IDocument
{
    public void Compose(IDocumentContainer container) =>
        BrandedPage.Compose(container, model, Title(model.ProductType), Content);

    private void Content(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(4);

            column.Item().Row(row =>
            {
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Issued to", model.RecipientName, 12));
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Booking reference", model.OrderNumber));
                row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Date issued", model.IssuedOn));
            });

            if (!string.IsNullOrWhiteSpace(model.SupplierReference))
            {
                column.Item().PaddingTop(10).Element(cell =>
                    BrandedPage.LabelledValue(cell, SupplierReferenceLabel(model.ProductType), model.SupplierReference, 22));
            }

            column.Item().Element(cell => BrandedPage.SectionHeading(cell, model.ProductTitle ?? "Your booking"));

            switch (model.ProductType)
            {
                case OrderLineItemType.Flight:
                    column.Item().Element(FlightSegments);
                    column.Item().Element(cell => Travellers(cell, "E-ticket number", traveller => traveller.TicketNumber));
                    break;

                case OrderLineItemType.Bus:
                    column.Item().Element(BusTrip);
                    column.Item().Element(cell => Travellers(cell, "Seat", traveller => traveller.Seat));
                    break;

                default:
                    // Tours, visas and group departures (M2): the title above says what it is, and
                    // the travellers are who it is for. Their own detail arrives with the catalog.
                    column.Item().Element(cell => Travellers(cell, null, _ => null));
                    break;
            }

            column.Item().Element(cell => BrandedPage.SectionHeading(cell, "Before you travel"));
            column.Item().Text(Guidance(model.ProductType)).FontSize(9);

            if (model.IssueNumber > 1 && model.SupersedesDocumentNumber is { } replaced)
            {
                column.Item().PaddingTop(8).Text($"This voucher replaces {replaced}, which should no longer be used.")
                    .FontSize(9).FontColor(BrandedPage.Muted);
            }
        });
    }

    private void FlightSegments(IContainer container)
    {
        if (model.Segments.Count == 0)
        {
            return;
        }

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(3);
                columns.RelativeColumn(3);
                columns.RelativeColumn(3);
            });

            table.Header(header =>
            {
                header.Cell().Element(BrandedPage.HeaderCell).Text("Flight").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Route").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Departs").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Arrives").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Cabin and bags").SemiBold();
            });

            foreach (var segment in model.Segments)
            {
                table.Cell().Element(BrandedPage.BodyCell).Text(segment.Carrier);
                table.Cell().Element(BrandedPage.BodyCell).Text($"{segment.From} → {segment.To}");
                table.Cell().Element(BrandedPage.BodyCell).Text(segment.DepartsAt);
                table.Cell().Element(BrandedPage.BodyCell).Text(segment.ArrivesAt ?? "—");
                table.Cell().Element(BrandedPage.BodyCell).Text(segment.Detail ?? "—");
            }
        });
    }

    private void BusTrip(IContainer container)
    {
        container.Column(column =>
        {
            foreach (var segment in model.Segments)
            {
                column.Item().PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Operator", segment.Carrier));
                    row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Departs", segment.DepartsAt));
                    row.RelativeItem().Element(cell => BrandedPage.LabelledValue(cell, "Arrives", segment.ArrivesAt));
                });
            }
        });
    }

    private void Travellers(IContainer container, string? detailHeading, Func<DocumentTraveller, string?> detail)
    {
        container.PaddingTop(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(5);
                columns.RelativeColumn(2);

                if (detailHeading is not null)
                {
                    columns.RelativeColumn(3);
                }
            });

            table.Header(header =>
            {
                header.Cell().Element(BrandedPage.HeaderCell).Text("Traveller").SemiBold();
                header.Cell().Element(BrandedPage.HeaderCell).Text("Type").SemiBold();

                if (detailHeading is not null)
                {
                    header.Cell().Element(BrandedPage.HeaderCell).Text(detailHeading).SemiBold();
                }
            });

            foreach (var traveller in model.Travellers)
            {
                table.Cell().Element(BrandedPage.BodyCell).Text(traveller.Name);
                table.Cell().Element(BrandedPage.BodyCell).Text(traveller.Type);

                if (detailHeading is not null)
                {
                    table.Cell().Element(BrandedPage.BodyCell).Text(detail(traveller) ?? "—");
                }
            }
        });
    }

    private static string Title(OrderLineItemType? productType) => productType switch
    {
        OrderLineItemType.Flight => "FLIGHT VOUCHER",
        OrderLineItemType.Bus => "BUS TICKET",
        _ => "VOUCHER",
    };

    private static string SupplierReferenceLabel(OrderLineItemType? productType) =>
        productType == OrderLineItemType.Flight ? "Airline booking reference (PNR)" : "Operator's reference";

    private static string Guidance(OrderLineItemType? productType) => productType switch
    {
        OrderLineItemType.Flight =>
            "Bring a valid photo ID whose name matches the traveller's name above exactly, and arrive at the airport "
            + "in good time: check-in closes before departure. Quote the airline booking reference if you are asked for it.",
        OrderLineItemType.Bus =>
            "Arrive at the terminal at least 30 minutes before departure with this ticket and a valid photo ID. "
            + "Seats are held only until boarding begins.",
        _ => "Show this voucher when you are asked for it, and quote the booking reference whenever you contact us.",
    };
}
