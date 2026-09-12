namespace TripsAgent.Application.Commerce;

/// <summary>
/// Where the payment gateway sends a traveller back to when they are done.
/// </summary>
/// <remarks>
/// <para>
/// Normally there is nothing here: the traveller came from the agency's own site, and that is where
/// they go back to — <c>IStorefrontDirectory.SiteUrlAsync</c> says where that is, so the page they
/// land on carries the agency's brand and never ours (CLAUDE.md rule 4). A value is configured only
/// where one storefront serves every agency in a development environment.
/// </para>
/// <para>
/// A traveller may ask for a particular page within their own agency's site. That is honoured only
/// when it really is within it — see <c>StorefrontCheckoutService.SafeReturnUrl</c>. Taking the URL
/// on trust would turn the checkout into an open redirect that a phishing page could borrow the
/// agency's domain for.
/// </para>
/// </remarks>
public sealed class CheckoutReturnUrl
{
    public CheckoutReturnUrl(string? value) =>
        Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The configured address, or null to use the agency's own site.</summary>
    public string? Value { get; }
}
