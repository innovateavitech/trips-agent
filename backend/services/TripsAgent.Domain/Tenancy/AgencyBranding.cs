using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy;

/// <summary>
/// How an agency looks to its own customers: logo, colours, type and contact details.
/// </summary>
/// <remarks>
/// <para>
/// Defined once here and reused by the storefront, invoices, vouchers and emails, so an agency
/// that changes its logo changes it everywhere at once.
/// </para>
/// <para>
/// This record is the reason nothing traveller-facing may hard-code the Trips brand: a traveller
/// sees the agent's business, never ours (CLAUDE.md rule 4).
/// </para>
/// </remarks>
public sealed class AgencyBranding : Entity, IAuditableEntity, ITenantOwnedEntity
{
    /// <summary>Used until an agency picks its own. Neutral rather than Trips blue, on purpose.</summary>
    public const string DefaultPrimaryColor = "#1F2933";

    /// <summary>The typeface a storefront falls back to.</summary>
    public const string DefaultFontFamily = "Inter";

    private AgencyBranding()
    {
        PrimaryColor = DefaultPrimaryColor;
        FontFamily = DefaultFontFamily;
    }

    /// <summary>Creates neutral branding for a newly registered agency.</summary>
    public static AgencyBranding CreateDefault(Agency agency)
    {
        ArgumentNullException.ThrowIfNull(agency);

        return new AgencyBranding
        {
            AgencyId = agency.Id,
            PrimaryColor = DefaultPrimaryColor,
            FontFamily = DefaultFontFamily,
        };
    }

    public Guid AgencyId { get; private set; }

    /// <summary>
    /// The agency's logo in the asset store. Null until one is uploaded, in which case the
    /// storefront falls back to the trading name as text.
    /// </summary>
    public Guid? LogoAssetId { get; private set; }

    /// <summary>Hex colour used for buttons and links on the storefront.</summary>
    public string PrimaryColor { get; private set; }

    /// <summary>Optional accent colour. Falls back to the primary when unset.</summary>
    public string? SecondaryColor { get; private set; }

    /// <summary>Typeface name for the storefront.</summary>
    public string FontFamily { get; private set; }

    /// <summary>Printed on invoices and vouchers, and shown in the storefront footer.</summary>
    public string? ContactAddress { get; private set; }

    /// <summary>Social profile URLs, keyed by network. JSON because the set of networks varies.</summary>
    public string SocialLinks { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Points branding at an uploaded logo, or clears it.</summary>
    public void SetLogo(Guid? assetId) => LogoAssetId = assetId;

    /// <summary>Sets the storefront colours. Both are validated as hex before they are stored.</summary>
    public void SetColors(string primary, string? secondary)
    {
        PrimaryColor = NormaliseHexColor(primary, nameof(primary))
            ?? throw new ArgumentException("A primary colour is required.", nameof(primary));

        SecondaryColor = NormaliseHexColor(secondary, nameof(secondary));
    }

    /// <summary>Sets the address printed on this agency's documents.</summary>
    public void SetContactAddress(string? address) =>
        ContactAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();

    private static string? NormaliseHexColor(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalised = value.Trim().ToUpperInvariant();

        // #RGB or #RRGGBB. Validated here and again by a CHECK constraint, because a malformed
        // colour renders an agency's whole storefront wrong and is tedious to trace back.
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                normalised, "^#([0-9A-F]{3}|[0-9A-F]{6})$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            throw new ArgumentException($"'{value}' is not a hex colour, e.g. '#325DEC'.", parameterName);
        }

        return normalised;
    }
}
