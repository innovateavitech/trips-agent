namespace TripsAgent.Application.Assets;

/// <summary>An agency's logo, ready to put on a page or in an email.</summary>
/// <param name="Png">The logo re-encoded as PNG, which every mail client and PDF reader shows.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public sealed record AgencyLogo(byte[] Png, int Width, int Height);

/// <summary>
/// Loads the logo an agency uploaded, for its documents and its travellers' email.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline stores images as WebP, which older Outlook desktop clients cannot show — and a
/// broken image at the top of an agency's email is worse than none. So the logo comes back as PNG.
/// </para>
/// <para>
/// Never throws over the logo itself: no logo, one not yet scanned, one missing from storage, or
/// one that will not decode all answer null, and the caller prints the agency's name instead. A
/// voucher without a logo is a nuisance; no voucher at all is a traveller turned away at check-in.
/// </para>
/// </remarks>
public interface IAgencyLogoSource
{
    /// <summary>The agency's servable logo as PNG, or null.</summary>
    public Task<AgencyLogo?> LoadAsync(Guid agencyId, CancellationToken cancellationToken = default);
}
