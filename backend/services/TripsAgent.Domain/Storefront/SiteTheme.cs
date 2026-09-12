using System.Text.Json;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// The parts of a site's look that belong to the site alone: its typography.
/// </summary>
/// <remarks>
/// <para>
/// The logo and colours are <b>not</b> here, although plan §2.4 lists them. They live on
/// <c>AgencyBranding</c>, which invoices, vouchers and emails read too — branding is defined once
/// (§2.2), and two copies would drift until an agent's invoice and website disagreed about their
/// own colours. The website builder's breakdown (W5) made this call.
/// </para>
/// <para>
/// <see cref="CustomCss"/> is kept from the plan and never written. Free-form CSS on a public page
/// can hide things from travellers, fake parts of the page, or bring back a brand the white-label
/// guarantee removed, in ways no test can catch. More theme settings are the better answer.
/// </para>
/// </remarks>
public sealed class SiteTheme : Entity, IAuditableEntity, ITenantScoped
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private SiteTheme()
    {
        Typography = "{}";
        Colors = "{}";
    }

    /// <summary>The theme for a newly created site, with the template's heading typeface.</summary>
    public static SiteTheme Create(Site site, string headingFont)
    {
        ArgumentNullException.ThrowIfNull(site);

        var theme = new SiteTheme { AgencyId = site.AgencyId, SiteId = site.Id };
        theme.SetHeadingFont(headingFont);
        return theme;
    }

    public Guid AgencyId { get; private set; }

    public Guid SiteId { get; private set; }

    /// <summary>Typography choices, as JSON: <c>{"headingFont":"lora"}</c>.</summary>
    public string Typography { get; private set; }

    /// <summary>Site-only colour choices beyond the agency's own. Empty in M2.</summary>
    public string Colors { get; private set; }

    /// <summary>Never written. See the remarks on this class.</summary>
    public string? CustomCss { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The typeface headings use, from <see cref="SiteFonts.All"/>.</summary>
    public string HeadingFont => ReadTypography().HeadingFont ?? SiteFonts.DefaultHeading;

    /// <summary>Changes the heading typeface. Only the fonts the storefront ships are allowed.</summary>
    public void SetHeadingFont(string headingFont)
    {
        var font = SiteFonts.Require(headingFont);
        Typography = JsonSerializer.Serialize(new TypographySettings(font), Json);
    }

    private TypographySettings ReadTypography()
    {
        try
        {
            return JsonSerializer.Deserialize<TypographySettings>(Typography, Json) ?? new TypographySettings(null);
        }
        catch (JsonException)
        {
            return new TypographySettings(null);
        }
    }

    private sealed record TypographySettings(string? HeadingFont);
}

/// <summary>
/// The typefaces a site may choose, each self-hosted by the storefront.
/// </summary>
/// <remarks>
/// An allowlist rather than a free choice: a font the storefront does not ship would fall back to
/// whatever the traveller's phone has, and a Google Fonts link would be a third-party request on
/// slow mobile data — and a tracker on the agent's site.
/// </remarks>
public static class SiteFonts
{
    public const string Inter = "inter";

    public const string DefaultHeading = Inter;

    /// <summary>Body text is always Inter: legible at small sizes, with tabular figures for prices.</summary>
    public const string Body = Inter;

    /// <summary>Inter only in the MVP; a choice of heading typeface comes after it.</summary>
    public static IReadOnlyList<string> All { get; } = [Inter];

    /// <summary>The font's canonical name, or throws when it is not one the storefront ships.</summary>
    public static string Require(string? font)
    {
        var candidate = font?.Trim().ToLowerInvariant();

        return candidate is not null && All.Contains(candidate, StringComparer.Ordinal)
            ? candidate
            : throw new ArgumentException($"Choose one of: {string.Join(", ", All)}.", nameof(font));
    }
}
