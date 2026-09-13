namespace TripsAgent.Application.Storefront;

/// <summary>
/// Which agency a storefront host name belongs to, and where an agency's storefront lives.
/// </summary>
/// <remarks>
/// <para>
/// The one thing the CRM needs from the storefront (F4), in both directions: an anonymous request to
/// the trip-request form or a quote link names a host, and the agency is found from it; a quote email
/// links to the agency's own site, never to ours (CLAUDE.md rule 4).
/// </para>
/// <para>
/// The storefront's domain model — <c>site_domains</c>, verified and primary — is built on its own
/// branch (#59). Until it lands, <c>PlaceholderStorefrontDirectory</c> answers with a clearly fake
/// host per agency; the storefront work replaces that registration with a lookup of the agency's
/// primary verified domain, and nothing in the CRM changes.
/// </para>
/// </remarks>
public interface IStorefrontDirectory
{
    /// <summary>The agency whose storefront answers on <paramref name="host"/>, or null when none does.</summary>
    /// <param name="host">Lower-case, without a port: <c>lekki-horizon.com</c>.</param>
    /// <remarks>
    /// Called for anonymous traffic, before any tenant is known, so an implementation reads across
    /// agencies — inside <c>IPlatformScope</c>, with its reason logged.
    /// </remarks>
    public Task<Guid?> FindAgencyAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>The root of the agency's storefront, such as <c>https://lekki-horizon.com</c>.</summary>
    public Task<Uri> SiteUrlAsync(Guid agencyId, CancellationToken cancellationToken = default);
}
