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
public sealed partial class AgencyBranding : Entity, IAuditableEntity, ITenantScoped
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

    /// <summary>Where travellers write to. Shown on the agency's website and its documents.</summary>
    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    /// <summary>A WhatsApp number in international form — how most Nigerian travel is actually sold.</summary>
    public string? WhatsAppNumber { get; private set; }

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

    /// <summary>Sets how travellers reach the agency. Each is optional; each is checked when given.</summary>
    public void SetContactDetails(string? email, string? phone, string? whatsAppNumber)
    {
        ContactEmail = Optional(email, 254, EmailPattern(), "an email address, e.g. hello@yourbusiness.com", nameof(email));
        ContactPhone = Optional(phone, 32, PhonePattern(), "a phone number, e.g. +234 803 123 4567", nameof(phone));
        WhatsAppNumber = Optional(whatsAppNumber, 32, PhonePattern(), "a WhatsApp number, e.g. +234 803 123 4567", nameof(whatsAppNumber));
    }

    /// <summary>True when <paramref name="email"/> is shaped like an email address.</summary>
    public static bool IsValidContactEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) && email.Trim().Length <= 254 && EmailPattern().IsMatch(email.Trim());

    /// <summary>True when <paramref name="phone"/> is shaped like a phone number, in local or international form.</summary>
    public static bool IsValidPhoneNumber(string phone) =>
        !string.IsNullOrWhiteSpace(phone) && phone.Trim().Length <= 32 && PhonePattern().IsMatch(phone.Trim());

    /// <summary>Replaces the social links. The caller has checked each network and address.</summary>
    public void SetSocialLinks(IReadOnlyDictionary<string, string> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        SocialLinks = System.Text.Json.JsonSerializer.Serialize(links);
    }

    private static string? Optional(
        string? value,
        int maxLength,
        System.Text.RegularExpressions.Regex pattern,
        string example,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength && pattern.IsMatch(trimmed)
            ? trimmed
            : throw new ArgumentException($"That is not {example}.", parameterName);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial System.Text.RegularExpressions.Regex EmailPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\+?[0-9][0-9 ()-]{5,30}$")]
    private static partial System.Text.RegularExpressions.Regex PhonePattern();

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
