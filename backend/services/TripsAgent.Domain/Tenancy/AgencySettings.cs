using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Tenancy;

/// <summary>
/// Operational preferences for one agency. One row per agency, created alongside it.
/// </summary>
/// <remarks>
/// Split from <see cref="Agency"/> rather than adding columns to it, because these change often
/// and independently — an agency edits its invoice prefix without touching anything KYB verified.
/// </remarks>
public sealed class AgencySettings : Entity, IAuditableEntity, ITenantOwnedEntity
{
    private AgencySettings()
    {
        SupportedCurrencies = [];
        InvoicePrefix = string.Empty;
        BookingReferencePrefix = string.Empty;
    }

    /// <summary>Creates the default settings for a newly registered agency.</summary>
    /// <remarks>
    /// The prefixes are derived from the slug so invoice numbers are readable from day one, and
    /// the only supported currency starts as the agency's own base currency.
    /// </remarks>
    public static AgencySettings CreateDefault(Agency agency)
    {
        ArgumentNullException.ThrowIfNull(agency);

        var prefix = DerivePrefix(agency.Slug);

        return new AgencySettings
        {
            AgencyId = agency.Id,
            SupportedCurrencies = [agency.BaseCurrency],
            InvoicePrefix = $"INV-{prefix}",
            BookingReferencePrefix = prefix,
        };
    }

    public Guid AgencyId { get; private set; }

    /// <summary>
    /// ISO 4217 codes this agency will quote in. Always contains its base currency.
    /// </summary>
    public IReadOnlyList<string> SupportedCurrencies { get; private set; }

    /// <summary>Leads every invoice number this agency issues, e.g. <c>INV-LAGOS</c>.</summary>
    public string InvoicePrefix { get; private set; }

    /// <summary>Leads every booking reference, so a traveller quoting one identifies the agency.</summary>
    public string BookingReferencePrefix { get; private set; }

    /// <summary>
    /// Which events this agency wants to hear about, and on which channel. Free-form JSON because
    /// the set of notifications is still growing; it is read by the notifications module, not here.
    /// </summary>
    public string NotificationPreferences { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Replaces the currencies this agency quotes in. The base currency is always kept.</summary>
    public void SetSupportedCurrencies(IEnumerable<string> currencies, string baseCurrency)
    {
        ArgumentNullException.ThrowIfNull(currencies);

        var normalised = currencies
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Append(baseCurrency.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        SupportedCurrencies = normalised;
    }

    /// <summary>Takes the first six alphanumeric characters of the slug, upper-cased.</summary>
    private static string DerivePrefix(string slug)
    {
        var letters = new string([.. slug.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        return letters.Length <= 6 ? letters : letters[..6];
    }
}
