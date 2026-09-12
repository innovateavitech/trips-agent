using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Commerce;

/// <summary>A link that was just issued: the row to save, and the address to put in an email.</summary>
public sealed record IssuedBookingLink(BookingAccessToken Token, string Secret);

/// <summary>
/// The links travellers manage their bookings with (build plan F5, decision 21 — no accounts).
/// </summary>
/// <remarks>
/// <para>
/// <b>The link is the credential.</b> There is nothing to sign in to, so whoever holds the link sees
/// the booking. That is why the secret is 256 random bits, why only its hash is stored, and why the
/// link expires. It is the same trade the CRM makes for a quote's public page.
/// </para>
/// <para>
/// <b>It always points at the agency's own site</b> (CLAUDE.md rule 4). The address comes from
/// <see cref="IStorefrontDirectory.SiteUrlAsync"/>, never from anything of ours, so a traveller who
/// follows it has no way to learn that a platform exists behind their travel agent.
/// </para>
/// </remarks>
public sealed class BookingAccessLinks
{
    /// <summary>The path on the agency's site that a link opens.</summary>
    public const string Path = "/bookings";

    private readonly IAppDbContext _db;
    private readonly IStorefrontDirectory _storefront;
    private readonly CommerceOptions _options;
    private readonly TimeProvider _clock;

    public BookingAccessLinks(
        IAppDbContext db,
        IStorefrontDirectory storefront,
        CommerceOptions options,
        TimeProvider clock)
    {
        _db = db;
        _storefront = storefront;
        _options = options;
        _clock = clock;
    }

    /// <summary>
    /// Issues a link for an order and stages it. The caller's save commits it with their own work.
    /// </summary>
    /// <returns>The token row, and the one and only copy of its secret.</returns>
    public IssuedBookingLink Issue(Guid agencyId, Guid orderId, DateTimeOffset now)
    {
        var (token, secret) = BookingAccessToken.Issue(agencyId, orderId, now, _options.BookingLinkLifetime);

        _db.BookingAccessTokens.Add(token);

        return new IssuedBookingLink(token, secret);
    }

    /// <summary>
    /// A working link for an order, issuing a fresh one when every link it had has lapsed.
    /// </summary>
    /// <remarks>
    /// Saves when it issues, because a caller asking for a URL is not in the middle of a unit of work
    /// of their own. A traveller who asks for their link again gets a new secret rather than the old
    /// one resent: the old one is not ours to resend, and may be in somebody else's inbox by now.
    /// </remarks>
    public async Task<string?> UrlForAsync(Guid agencyId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // An existing link's secret cannot be read back — only its hash was kept — so asking for a
        // link always issues a fresh one. The old ones keep working until they lapse, which is what
        // a traveller who bookmarked the first email expects.
        var issued = Issue(agencyId, orderId, now);

        await _db.SaveChangesAsync(cancellationToken);

        return await UrlAsync(agencyId, issued.Secret, cancellationToken);
    }

    /// <summary>The address to put in an email, on the agency's own site.</summary>
    public async Task<string> UrlAsync(Guid agencyId, string secret, CancellationToken cancellationToken = default)
    {
        var site = await _storefront.SiteUrlAsync(agencyId, cancellationToken);

        return new Uri(site, $"{Path}/{secret}").ToString();
    }

    /// <summary>
    /// The order a presented link opens, or null when it opens nothing.
    /// </summary>
    /// <remarks>
    /// Looked up by the hash of the secret, inside the tenant the caller's host already resolved to —
    /// so a link for one agency's booking presented on another agency's domain finds nothing. A
    /// revoked or lapsed link is "nothing" as well, and reads to the caller exactly like a wrong one.
    /// </remarks>
    public async Task<Guid?> OrderForAsync(string? secret, CancellationToken cancellationToken = default)
    {
        var tidy = secret?.Trim();

        if (!LooksLikeSecret(tidy))
        {
            return null;
        }

        var hash = BookingAccessToken.HashOf(tidy!);
        var token = await _db.BookingAccessTokens.FirstOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);

        var now = _clock.GetUtcNow();

        if (token is null || !token.IsUsableAt(now))
        {
            return null;
        }

        token.RecordUse(now);
        await _db.SaveChangesAsync(cancellationToken);

        return token.OrderId;
    }

    /// <summary>True for something shaped like a secret, so a malformed one never reaches the database.</summary>
    internal static bool LooksLikeSecret(string? secret) =>
        secret is { Length: 43 }
        && secret.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
