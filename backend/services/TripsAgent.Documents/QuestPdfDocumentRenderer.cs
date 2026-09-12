using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;
using TripsAgent.Domain.Documents;

namespace TripsAgent.Documents;

/// <summary>
/// <see cref="IDocumentRenderer"/> with QuestPDF: picks the template for the document and product,
/// and draws it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same model gives the same bytes.</b> The PDF's creation and modification dates are the
/// document's own issue time rather than the time it was drawn, and only the fonts bundled with
/// QuestPDF are used. Nothing else in a QuestPDF file varies between runs, so rendering a model twice
/// produces identical files — a unit test holds it to that.
/// </para>
/// <para>
/// Nothing in the file names us: the author, creator and producer are the agency.
/// </para>
/// </remarks>
public sealed class QuestPdfDocumentRenderer : IDocumentRenderer
{
    public QuestPdfDocumentRenderer(LicenseType license)
    {
        QuestPdfSettings.Apply(license);
    }

    public RenderedDocument Render(DocumentRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var template = DocumentTemplates.For(model.DocumentType, model.ProductType);

        IDocument document = model.DocumentType == DocumentType.Invoice
            ? new InvoiceDocument(model)
            : new VoucherDocument(model);

        var pdf = Document.Create(document.Compose)
            .WithMetadata(new DocumentMetadata
            {
                Title = $"{model.DocumentType} {model.DocumentNumber}",
                Author = model.Brand.Name,
                Creator = model.Brand.Name,
                Producer = model.Brand.Name,
                Subject = $"Booking {model.OrderNumber}",
                Language = "en",
                CreationDate = model.IssuedAt,
                ModifiedDate = model.IssuedAt,
            })
            .GeneratePdf();

        return new RenderedDocument(pdf, template.Key, template.Version);
    }
}

/// <summary>QuestPDF's process-wide settings, applied once.</summary>
internal static class QuestPdfSettings
{
    private static readonly Lock Gate = new();

    public static void Apply(LicenseType license)
    {
        lock (Gate)
        {
            QuestPDF.Settings.License = license;

            // The bundled Lato family only: the machine's own fonts differ from a laptop to a
            // container, and a document must look — and hash — the same wherever it is drawn.
            QuestPDF.Settings.UseEnvironmentFonts = false;

            // A traveller's name in a script Lato does not cover prints as blank boxes rather than
            // failing the whole voucher. Names on travel documents are Latin script in practice.
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
        }
    }
}
