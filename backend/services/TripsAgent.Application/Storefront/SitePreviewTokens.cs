using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Application.Storefront;

/// <summary>What a valid preview link lets its holder see: one version of one agency's site, until it expires.</summary>
public sealed record SitePreviewGrant(Guid AgencyId, Guid SiteId, Guid VersionId, DateTimeOffset ExpiresAt);

/// <summary>
/// Signs and checks the short-lived links an agent uses to see an unpublished version of their site.
/// </summary>
/// <remarks>
/// <para>
/// The token names the version, the site and the agency, and carries a keyed hash of all three plus
/// its expiry. Nothing is stored: a link cannot be widened to another version or kept alive past its
/// expiry without the server's key. It is a credential — like the signed KYB document link — which is
/// why the preview page is the one storefront page that does not take its tenant from the Host header.
/// </para>
/// <para>
/// The hash is the platform's token HMAC, with a purpose prefix so a preview token can never be
/// mistaken for any other secret that key signs.
/// </para>
/// </remarks>
public sealed class SitePreviewTokens
{
    private const string Purpose = "site-preview:v1:";

    private readonly ITokenHasher _hasher;
    private readonly TimeProvider _clock;
    private readonly StorefrontOptions _options;

    public SitePreviewTokens(ITokenHasher hasher, TimeProvider clock, StorefrontOptions options)
    {
        _hasher = hasher;
        _clock = clock;
        _options = options;
    }

    /// <summary>A token for <paramref name="versionId"/>, and when it stops working.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid agencyId, Guid siteId, Guid versionId)
    {
        var lifetime = _options.PreviewLinkLifetime <= StorefrontOptions.MaxPreviewLinkLifetime
            ? _options.PreviewLinkLifetime
            : StorefrontOptions.MaxPreviewLinkLifetime;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{agencyId:N}.{siteId:N}.{versionId:N}.{expiresAt.ToUnixTimeSeconds()}");

        return ($"{payload}.{Sign(payload)}", expiresAt);
    }

    /// <summary>What <paramref name="token"/> grants, or null when it is malformed, forged or expired.</summary>
    public SitePreviewGrant? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 400)
        {
            return null;
        }

        var parts = token.Split('.');

        if (parts.Length != 5
            || !Guid.TryParseExact(parts[0], "N", out var agencyId)
            || !Guid.TryParseExact(parts[1], "N", out var siteId)
            || !Guid.TryParseExact(parts[2], "N", out var versionId)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return null;
        }

        var payload = string.Join('.', parts[..4]);
        var expected = Encoding.ASCII.GetBytes(Sign(payload));
        var given = Encoding.ASCII.GetBytes(parts[4]);

        // Constant time: comparing byte by byte and stopping at the first difference tells an attacker,
        // one timing at a time, how much of their forgery was right.
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);

        return expiresAt > _clock.GetUtcNow() ? new SitePreviewGrant(agencyId, siteId, versionId, expiresAt) : null;
    }

    private string Sign(string payload) =>
        _hasher.Hash(Purpose + payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
