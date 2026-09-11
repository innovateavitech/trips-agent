using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Documents;

/// <summary>One template: its stable key and the version of its layout.</summary>
/// <param name="Key">Recorded on every document it draws, e.g. <c>voucher.flight</c>. Never renamed.</param>
/// <param name="Version">
/// Bumped whenever the layout or wording changes. A document records the version that drew it, so
/// "what did this customer's voucher look like in March" has an answer.
/// </param>
public sealed record DocumentTemplate(string Key, int Version);

/// <summary>
/// Which template draws which document (#46).
/// </summary>
/// <remarks>
/// <para>
/// Full vouchers for flight and bus, which M1 sells. Tours, visas and group departures arrive with
/// the M2 catalog; until then one plain voucher serves all three, printing what an order line
/// already holds — the product's title, the travellers and the booking reference. Giving one of
/// them a layout of its own later is a new key here, not a change to documents already issued.
/// </para>
/// <para>
/// One invoice for every product type, because an invoice is about the order and an order may mix
/// products. Each of its lines says what kind of product it is.
/// </para>
/// </remarks>
public static class DocumentTemplates
{
    public static readonly DocumentTemplate Invoice = new("invoice.standard", 1);

    public static readonly DocumentTemplate FlightVoucher = new("voucher.flight", 1);

    public static readonly DocumentTemplate BusVoucher = new("voucher.bus", 1);

    /// <summary>Tours, visas and group departures, until the catalog gives them more to print.</summary>
    public static readonly DocumentTemplate PlainVoucher = new("voucher.plain", 1);

    /// <summary>Every template this build has.</summary>
    public static IReadOnlyList<DocumentTemplate> All { get; } = [Invoice, FlightVoucher, BusVoucher, PlainVoucher];

    /// <summary>The template for a document of <paramref name="documentType"/>, for <paramref name="productType"/>.</summary>
    /// <exception cref="NotSupportedException">No template draws that document.</exception>
    public static DocumentTemplate For(DocumentType documentType, OrderLineItemType? productType) =>
        (documentType, productType) switch
        {
            (DocumentType.Invoice, _) => Invoice,
            (DocumentType.Voucher, OrderLineItemType.Flight) => FlightVoucher,
            (DocumentType.Voucher, OrderLineItemType.Bus) => BusVoucher,
            (DocumentType.Voucher, OrderLineItemType.Tour or OrderLineItemType.Visa or OrderLineItemType.GroupDeparture) => PlainVoucher,
            _ => throw new NotSupportedException(
                $"There is no template for a {documentType}{(productType is null ? string.Empty : $" for a {productType}")}."),
        };
}
