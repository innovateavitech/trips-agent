namespace TripsAgent.Application.Notifications;

/// <summary>
/// Spots our own brand in text a traveller is about to read — an email, an invoice, a voucher.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md rule 4: a traveller must never learn Trips exists. Every traveller-facing rendering is
/// checked against these before it leaves, as the belt to the templates' brace.
/// </para>
/// <para>
/// Not the bare word "Trips": an agency may well be called "Lagos Trips Ltd", and blocking its own
/// name from its own mail would be absurd. These are the forms our brand actually takes — the
/// product name and the domain.
/// </para>
/// </remarks>
public static class TravellerBrandGuard
{
    private static readonly string[] PlatformMarkers = [NotificationTemplateCatalog.ProductName, "tripsagent"];

    /// <summary>True when <paramref name="text"/> would give our identity away.</summary>
    public static bool MentionsPlatform(string? text) =>
        !string.IsNullOrEmpty(text)
        && PlatformMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
