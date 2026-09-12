using System.Globalization;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Documents;

/// <summary>The agency, as its documents print it. Never ours — CLAUDE.md rule 4.</summary>
/// <param name="Name">The agency's trading name, or its legal name when it trades under no other.</param>
/// <param name="PrimaryColor">A hex colour from <c>agency_branding</c>, for the header band.</param>
/// <param name="Logo">The logo as PNG bytes, or null to print the name instead.</param>
/// <param name="ContactAddress">From <c>agency_branding</c>, printed in the header and the footer.</param>
/// <param name="TaxId">The agency's tax identification number, printed on its invoices.</param>
public sealed record DocumentBrand(string Name, string PrimaryColor, byte[]? Logo, string? ContactAddress, string? TaxId);

/// <summary>One traveller, as a voucher lists them.</summary>
/// <param name="Name">As on their ID, which is what the carrier checks.</param>
/// <param name="Type">Adult, Child or Infant.</param>
/// <param name="TicketNumber">The e-ticket number, once issued. A bus ticket has none.</param>
/// <param name="Seat">The seat, when the operator assigned one.</param>
public sealed record DocumentTraveller(string Name, string Type, string? TicketNumber, string? Seat);

/// <summary>One leg of a journey, in the wall-clock times a ticket prints.</summary>
/// <param name="Carrier">"Air Peace P4 7121", or "GIG Mobility".</param>
/// <param name="From">
/// Where it leaves from — an airport code. Null when all we hold is a supplier's internal id, as for
/// a bus terminal: the product's title names the route instead.
/// </param>
/// <param name="To">Where it arrives, on the same terms.</param>
/// <param name="DepartsAt">"Fri 2 Oct 2026, 06:45", in the time where it leaves from.</param>
/// <param name="ArrivesAt">Likewise where it arrives, or null when the carrier did not say.</param>
/// <param name="Detail">Cabin and baggage for a flight. Null for nothing to add.</param>
public sealed record DocumentSegment(string Carrier, string? From, string? To, string DepartsAt, string? ArrivesAt, string? Detail);

/// <summary>One line of an invoice.</summary>
/// <param name="Description">What was bought, as it read when it was sold.</param>
/// <param name="ProductType">Which kind of product, so the line can say so.</param>
/// <param name="Travellers">How many people it is for.</param>
/// <param name="AmountMinor">What the traveller paid for it, tax included, in minor units.</param>
public sealed record DocumentLineItem(string Description, OrderLineItemType ProductType, int Travellers, long AmountMinor);

/// <summary>
/// Everything a template prints, already worked out. A template looks nothing up.
/// </summary>
/// <remarks>
/// <para>
/// Built by <see cref="OrderDocumentService"/> from the order, its travellers and the supplier's
/// booking, then handed to <see cref="IDocumentRenderer"/>. Keeping the lookups out of the templates
/// is what lets the templates be tested with nothing but a model, and what lets the service check
/// every string on the page for our brand before anything is drawn.
/// </para>
/// <para>
/// There is no net rate and no markup anywhere in it, on purpose. The traveller sees what they
/// paid. The tax on the markup is not shown either: at a known VAT rate it gives the markup away,
/// and with it the net rate (open question 25 decides what a tax invoice here must carry).
/// </para>
/// </remarks>
public sealed record DocumentRenderModel
{
    public required DocumentType DocumentType { get; init; }

    public required string DocumentNumber { get; init; }

    /// <summary>1 for a first issue. Anything higher prints as a replacement.</summary>
    public required int IssueNumber { get; init; }

    /// <summary>The number of the document this one replaces, for a reissue.</summary>
    public string? SupersedesDocumentNumber { get; init; }

    /// <summary>The instant the number was taken. Also the PDF's creation date, so a render is repeatable.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary><see cref="IssuedAt"/> as a date in the agency's own time zone: "11 September 2026".</summary>
    public required string IssuedOn { get; init; }

    /// <summary>The booking reference the traveller quotes.</summary>
    public required string OrderNumber { get; init; }

    /// <summary>The customer's currency — the order's, which every line shares.</summary>
    public required string Currency { get; init; }

    public required DocumentBrand Brand { get; init; }

    /// <summary>Who it is made out to.</summary>
    public required string RecipientName { get; init; }

    // ----------------------------------------------------------------- invoice

    public IReadOnlyList<DocumentLineItem> Lines { get; init; } = [];

    /// <summary>What the traveller paid in all, tax included.</summary>
    public long TotalMinor { get; init; }

    /// <summary>"Paid", "Awaiting payment", …</summary>
    public string? PaymentStatus { get; init; }

    // ----------------------------------------------------------------- voucher

    /// <summary>What the voucher is for, which decides its template.</summary>
    public OrderLineItemType? ProductType { get; init; }

    /// <summary>What was bought, as it read when it was sold.</summary>
    public string? ProductTitle { get; init; }

    /// <summary>The airline's or operator's own reference: the PNR.</summary>
    public string? SupplierReference { get; init; }

    public IReadOnlyList<DocumentTraveller> Travellers { get; init; } = [];

    public IReadOnlyList<DocumentSegment> Segments { get; init; } = [];

    /// <summary>Every string the traveller will read, for the brand check.</summary>
    public IEnumerable<string?> PrintedText()
    {
        yield return DocumentNumber;
        yield return SupersedesDocumentNumber;
        yield return IssuedOn;
        yield return OrderNumber;
        yield return Brand.Name;
        yield return Brand.ContactAddress;
        yield return Brand.TaxId;
        yield return RecipientName;
        yield return PaymentStatus;
        yield return ProductTitle;
        yield return SupplierReference;

        foreach (var line in Lines)
        {
            yield return line.Description;
        }

        foreach (var traveller in Travellers)
        {
            yield return traveller.Name;
            yield return traveller.TicketNumber;
            yield return traveller.Seat;
        }

        foreach (var segment in Segments)
        {
            yield return segment.Carrier;
            yield return segment.From;
            yield return segment.To;
            yield return segment.Detail;
        }
    }
}

/// <summary>A rendered PDF, and the template that drew it.</summary>
/// <param name="Pdf">The file.</param>
/// <param name="TemplateKey">Which template, e.g. <c>voucher.flight</c>.</param>
/// <param name="TemplateVersion">Which version of it — recorded on the document for good.</param>
public sealed record RenderedDocument(byte[] Pdf, string TemplateKey, int TemplateVersion);

/// <summary>
/// Draws an invoice or a voucher as a PDF. A port: Application knows nothing about the PDF library.
/// </summary>
/// <remarks>
/// Implementations pick the template from the document type and, for a voucher, the product type —
/// flight, bus, tour, visa or group departure. The same model must always produce the same bytes,
/// so a render can be repeated and compared.
/// </remarks>
public interface IDocumentRenderer
{
    public RenderedDocument Render(DocumentRenderModel model);
}

/// <summary>Money as a document prints it: the currency code, then the amount — <c>NGN 142,500.00</c>.</summary>
/// <remarks>
/// The code rather than a symbol. It reads the same in every font and every language, and an
/// invoice is no place for a traveller to wonder which dollar is meant.
/// </remarks>
public static class DocumentMoney
{
    // ISO 4217 currencies with no minor unit. Everything else gets two decimals.
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    public static string Format(long amountMinor, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var code = currency.Trim().ToUpperInvariant();

        if (ZeroDecimalCurrencies.Contains(code))
        {
            return $"{code} {amountMinor.ToString("N0", CultureInfo.InvariantCulture)}";
        }

        var sign = amountMinor < 0 ? "-" : string.Empty;
        var major = Math.DivRem(Math.Abs(amountMinor), 100, out var minor);

        return $"{code} {sign}{major.ToString("N0", CultureInfo.InvariantCulture)}.{minor.ToString("00", CultureInfo.InvariantCulture)}";
    }
}
