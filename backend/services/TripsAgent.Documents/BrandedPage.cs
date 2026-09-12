using System.Globalization;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TripsAgent.Application.Documents;

namespace TripsAgent.Documents;

/// <summary>
/// The page every document is drawn on: the agency's colour, its logo or its name, and its
/// contact details. Never ours — CLAUDE.md rule 4.
/// </summary>
/// <remarks>
/// <para>
/// The only colours here are the agency's own and three neutrals for text and rules. The header
/// band is the agency's primary colour, and its text is white or near-black, whichever reads on it.
/// </para>
/// <para>
/// Fonts are the Lato family bundled with QuestPDF and nothing from the machine, so a document
/// renders the same on a laptop and in a container — which is what lets the same model produce the
/// same bytes wherever it is drawn.
/// </para>
/// </remarks>
internal static partial class BrandedPage
{
    public const string FontFamily = "Lato";

    public static readonly Color Ink = Color.FromHex("#1F2933");

    public static readonly Color Muted = Color.FromHex("#616E7C");

    public static readonly Color Rule = Color.FromHex("#D9DEE3");

    private static readonly Color LightInk = Color.FromHex("#FFFFFF");

    /// <summary>Lays out one page with the agency's header and footer, and <paramref name="content"/> between.</summary>
    public static void Compose(
        IDocumentContainer container,
        DocumentRenderModel model,
        string title,
        Action<IContainer> content)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(36);
            page.MarginVertical(32);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(10).FontColor(Ink));

            page.Header().Element(header => Header(header, model, title));
            page.Content().PaddingVertical(18).Element(content);
            page.Footer().Element(footer => Footer(footer, model));
        });
    }

    /// <summary>A small grey label over a value: "Booking reference" over "ORD-2026-000142".</summary>
    public static void LabelledValue(IContainer container, string label, string? value, float valueSize = 11)
    {
        container.Column(column =>
        {
            column.Item().Text(label).FontSize(8).FontColor(Muted);
            column.Item().Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(valueSize).SemiBold();
        });
    }

    /// <summary>A section heading with a rule under it.</summary>
    public static void SectionHeading(IContainer container, string text)
    {
        container.PaddingTop(14).PaddingBottom(6).BorderBottom(1).BorderColor(Rule)
            .Text(text).FontSize(11).Bold();
    }

    /// <summary>A header cell for a table.</summary>
    public static IContainer HeaderCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Rule).PaddingVertical(5).PaddingHorizontal(4);

    /// <summary>A body cell for a table.</summary>
    public static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Rule).PaddingVertical(6).PaddingHorizontal(4);

    /// <summary>
    /// The agency's colour as six hex digits, or the neutral default if it is not a colour. Branding
    /// is validated when it is saved; this is the belt to that brace.
    /// </summary>
    public static string SafeHex(string? color)
    {
        if (color is null || !HexColor().IsMatch(color))
        {
            return TripsAgent.Domain.Tenancy.AgencyBranding.DefaultPrimaryColor;
        }

        var digits = color[1..];

        return digits.Length == 3
            ? "#" + string.Concat(digits.Select(digit => $"{digit}{digit}"))
            : color;
    }

    /// <summary>White on a dark brand colour, near-black on a light one — whichever can be read.</summary>
    public static Color TextOn(string hex)
    {
        var red = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        // Perceived brightness (ITU-R BT.601), in whole numbers: 0 is black, 255 000 is white.
        var brightness = (299 * red) + (587 * green) + (114 * blue);

        return brightness > 150_000 ? Ink : LightInk;
    }

    private static void Header(IContainer container, DocumentRenderModel model, string title)
    {
        var band = SafeHex(model.Brand.PrimaryColor);
        var onBand = TextOn(band);

        container.Column(column =>
        {
            column.Item().Background(Color.FromHex(band)).Padding(16).Row(row =>
            {
                row.RelativeItem().AlignMiddle().Element(brand =>
                {
                    if (model.Brand.Logo is { Length: > 0 } logo)
                    {
                        brand.Height(40).AlignLeft().Image(logo).FitHeight();
                    }
                    else
                    {
                        brand.Text(model.Brand.Name).FontSize(18).Bold().FontColor(onBand);
                    }
                });

                row.RelativeItem().AlignRight().AlignMiddle().Column(heading =>
                {
                    heading.Item().AlignRight().Text(title).FontSize(16).Bold().FontColor(onBand);
                    heading.Item().AlignRight().Text(model.DocumentNumber).FontSize(10).FontColor(onBand);

                    if (model.IssueNumber > 1)
                    {
                        heading.Item().AlignRight()
                            .Text(model.SupersedesDocumentNumber is { } replaced
                                ? $"Issue {model.IssueNumber} — replaces {replaced}"
                                : $"Issue {model.IssueNumber}")
                            .FontSize(9).FontColor(onBand);
                    }
                });
            });

            column.Item().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Text(model.Brand.Logo is { Length: > 0 } ? model.Brand.Name : string.Empty).SemiBold();
                row.RelativeItem().AlignRight().Text(model.Brand.ContactAddress ?? string.Empty).FontSize(9).FontColor(Muted);
            });
        });
    }

    private static void Footer(IContainer container, DocumentRenderModel model)
    {
        container.BorderTop(1).BorderColor(Rule).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                text.Span(model.Brand.Name);

                if (!string.IsNullOrWhiteSpace(model.Brand.ContactAddress))
                {
                    text.Span($" · {model.Brand.ContactAddress}");
                }
            });

            row.ConstantItem(80).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColor();
}
