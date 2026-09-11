using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Notifications;

/// <summary>Whose mail this appears to be, as the renderer needs it.</summary>
/// <param name="Name">Shown in the header, the footer, the greeting line and the From name.</param>
/// <param name="Color">A hex colour for the traveller layout's header. Ignored by the agency layout.</param>
/// <param name="LogoUrl">An absolute https URL, or null to show the name as text instead.</param>
/// <param name="Contact">The footer's contact line, or null.</param>
/// <param name="ReplyTo">Where a reply should go, or null for the sender's default.</param>
public sealed record NotificationBrand(
    string Name,
    string Color,
    string? LogoUrl,
    string? Contact,
    string? ReplyTo)
{
    /// <summary>Ours, for mail to agency staff. Never used for a traveller — see <see cref="NotificationRenderer"/>.</summary>
    public static NotificationBrand Platform { get; } = new(
        NotificationTemplateCatalog.ProductName, AgencyBranding.DefaultPrimaryColor, null, null, null);
}

/// <summary>A rendered message, ready to hand to a channel.</summary>
public sealed record RenderedNotification(string Subject, string Html, string Text);

/// <summary>
/// A template could not be rendered, and rendering it again will not help.
/// </summary>
/// <remarks>
/// A missing variable or a traveller message about to carry our brand is a bug in the code that
/// queued it, not a transient fault — so the dispatcher marks the notification failed rather than
/// retrying it into the dead-letter queue five times.
/// </remarks>
public sealed class NotificationRenderException : Exception
{
    public NotificationRenderException(string message)
        : base(message)
    {
    }

    public NotificationRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NotificationRenderException()
        : base("The notification could not be rendered.")
    {
    }
}

/// <summary>
/// Fills a template's <c>{{token}}</c> placeholders, and refuses to produce traveller mail that
/// would show the Trips brand.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a string replacer and not a template engine. The templates are ours, in code,
/// reviewed in pull requests; they need substitution and one conditional block, and anything more
/// capable is more surface for a template to do something surprising in an email.
/// </para>
/// <para>
/// <b>Every value is HTML-encoded in the HTML body.</b> Business names, rejection reasons and
/// traveller names are typed by people; one of them containing <c>&lt;a href=…&gt;</c> must arrive
/// as text, not as a working link inside an email that looks like it came from the agency.
/// </para>
/// </remarks>
public static partial class NotificationRenderer
{
    // Markers around the two alternatives in the traveller header. HTML comments, so a template
    // that reached a client unrendered would still display correctly.
    public const string LogoBlockOpen = "<!--logo-->";
    public const string LogoBlockClose = "<!--/logo-->";
    public const string NameBlockOpen = "<!--name-->";
    public const string NameBlockClose = "<!--/name-->";

    /// <summary>
    /// Strings that give our identity away. Checked against every traveller-facing rendering.
    /// </summary>
    /// <remarks>
    /// Not the bare word "Trips": an agency may well be called "Lagos Trips Ltd", and blocking its
    /// own name from its own mail would be absurd. These are the forms our brand actually takes —
    /// the product name and the domain.
    /// </remarks>
    private static readonly string[] PlatformMarkers = [NotificationTemplateCatalog.ProductName, "tripsagent"];

    /// <summary>Renders <paramref name="template"/> for one recipient.</summary>
    /// <param name="template">The version to render.</param>
    /// <param name="brand">Whose brand wraps it. Must be the agency's for a traveller-facing template.</param>
    /// <param name="recipientName">Who it is addressed to.</param>
    /// <param name="values">The template's own variables.</param>
    /// <exception cref="NotificationRenderException">
    /// A variable is missing, or a traveller-facing message would carry the Trips brand.
    /// </exception>
    public static RenderedNotification Render(
        NotificationTemplate template,
        NotificationBrand brand,
        string recipientName,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(values);

        if (template.Audience == NotificationAudience.Traveller)
        {
            GuardTravellerBrand(template, brand);
        }

        var logoUrl = SafeLogoUrl(brand.LogoUrl);

        var all = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            [NotificationTemplateCatalog.BrandNameToken] = brand.Name,
            [NotificationTemplateCatalog.BrandColorToken] = SafeColor(brand.Color),
            [NotificationTemplateCatalog.BrandLogoUrlToken] = logoUrl ?? string.Empty,
            [NotificationTemplateCatalog.BrandContactToken] = brand.Contact ?? string.Empty,
            [NotificationTemplateCatalog.RecipientNameToken] = recipientName ?? string.Empty,
        };

        // One header or the other, never both and never an <img src="">, which several clients
        // draw as a broken-image icon at the top of the agency's email.
        var html = logoUrl is null
            ? RemoveBlock(template.HtmlTemplate, LogoBlockOpen, LogoBlockClose)
            : RemoveBlock(template.HtmlTemplate, NameBlockOpen, NameBlockClose);

        var rendered = new RenderedNotification(

            // A subject is a mail header: a value carrying a line break could start a new one.
            Fill(template.SubjectTemplate, all, WithoutLineBreaks, template.Key),
            Fill(html, all, WebUtility.HtmlEncode, template.Key),
            Fill(template.TextTemplate, all, value => value, template.Key));

        if (template.Audience == NotificationAudience.Traveller)
        {
            GuardTravellerOutput(template, rendered);
        }

        return rendered;
    }

    /// <summary>The tokens a template body uses, in order of first appearance.</summary>
    public static IReadOnlyList<string> TokensIn(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return TokenPattern().Matches(template)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void GuardTravellerBrand(NotificationTemplate template, NotificationBrand brand)
    {
        if (string.IsNullOrWhiteSpace(brand.Name) || ContainsPlatformMarker(brand.Name))
        {
            throw new NotificationRenderException(
                $"Template '{template.Key}' is traveller-facing and must carry the agency's own brand, not "
                + $"'{brand.Name}'. A traveller must never learn Trips exists (CLAUDE.md rule 4).");
        }
    }

    private static void GuardTravellerOutput(NotificationTemplate template, RenderedNotification rendered)
    {
        // The belt to the brace above: catches our name arriving through a variable, or a template
        // edited to mention us, before a single traveller sees it.
        foreach (var part in new[] { rendered.Subject, rendered.Html, rendered.Text })
        {
            if (ContainsPlatformMarker(part))
            {
                throw new NotificationRenderException(
                    $"Template '{template.Key}' v{template.Version} rendered with the Trips brand in a "
                    + "traveller-facing message. Refusing to send it (CLAUDE.md rule 4).");
            }
        }
    }

    private static bool ContainsPlatformMarker(string value) =>
        PlatformMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Fill(
        string template,
        Dictionary<string, string> values,
        Func<string, string> encode,
        string templateKey)
    {
        var missing = new List<string>();

        var filled = TokenPattern().Replace(template, match =>
        {
            var token = match.Groups[1].Value;

            if (values.TryGetValue(token, out var value))
            {
                return encode(value);
            }

            missing.Add(token);
            return match.Value;
        });

        if (missing.Count > 0)
        {
            throw new NotificationRenderException(
                $"Template '{templateKey}' needs {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}, "
                + "which the notification did not supply.");
        }

        return filled;
    }

    private static string RemoveBlock(string html, string open, string close)
    {
        var builder = new StringBuilder(html);

        int start;
        while ((start = builder.ToString().IndexOf(open, StringComparison.Ordinal)) >= 0)
        {
            var end = builder.ToString().IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            builder.Remove(start, end + close.Length - start);
        }

        // Markers of the block that stays are harmless but noisy in the source of the email.
        return builder.ToString()
            .Replace(LogoBlockOpen, string.Empty, StringComparison.Ordinal)
            .Replace(LogoBlockClose, string.Empty, StringComparison.Ordinal)
            .Replace(NameBlockOpen, string.Empty, StringComparison.Ordinal)
            .Replace(NameBlockClose, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The colour, if it is one. It lands inside a <c>style</c> attribute, where anything other
    /// than a hex colour is a way to inject CSS into the agency's email.
    /// </summary>
    private static string SafeColor(string? color) =>
        color is not null && HexColorPattern().IsMatch(color) ? color : AgencyBranding.DefaultPrimaryColor;

    /// <summary>An absolute https URL, or null. Anything else is not something to point an img at.</summary>
    private static string? SafeLogoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.AbsoluteUri
            : null;

    private static string WithoutLineBreaks(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColorPattern();
}
