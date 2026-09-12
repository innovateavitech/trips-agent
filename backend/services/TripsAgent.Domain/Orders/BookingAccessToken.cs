using System.Security.Cryptography;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// The link a traveller manages their booking with, since they have no account to sign in to
/// (decision 21).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the hash is stored.</b> The secret exists once, in the email that carries it; this row
/// holds SHA-256 of it and nothing else. A dump of this table lets nobody open anybody's booking,
/// which matters because the page behind the link shows a traveller's name, itinerary and documents.
/// </para>
/// <para>
/// <b>It expires.</b> A link is useless a long time after the trip, and one that lives forever sits
/// in an inbox forever. <see cref="Issue"/> takes the lifetime; the traveller can always ask for a
/// fresh link, which issues a new secret rather than resending the old one.
/// </para>
/// <para>
/// <b>It is a bearer token, so it grants exactly one order.</b> Everything the page shows is read
/// through the order it names, inside that order's own agency — never by anything the caller sends.
/// </para>
/// </remarks>
public sealed class BookingAccessToken : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>How long a link lasts unless a caller says otherwise.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    /// <summary>Bytes of randomness in a secret. 32 is 256 bits — not guessable, and still a short URL.</summary>
    public const int SecretBytes = 32;

    private BookingAccessToken() => TokenHash = string.Empty;

    /// <summary>
    /// Issues a link for an order, and hands back the one and only copy of its secret.
    /// </summary>
    /// <param name="agencyId">The agency whose order it is.</param>
    /// <param name="orderId">The order the link opens.</param>
    /// <param name="now">When it was issued.</param>
    /// <param name="lifetime">How long it lasts. <see cref="DefaultLifetime"/> when null.</param>
    /// <returns>The row to save, and the secret to put in the email. The secret is never stored.</returns>
    public static (BookingAccessToken Token, string Secret) Issue(
        Guid agencyId,
        Guid orderId,
        DateTimeOffset now,
        TimeSpan? lifetime = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);

        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

        var token = new BookingAccessToken
        {
            AgencyId = agencyId,
            OrderId = orderId,
            TokenHash = HashOf(secret),
            IssuedAt = now,
            ExpiresAt = now + (lifetime ?? DefaultLifetime),
        };

        return (token, secret);
    }

    /// <summary>
    /// SHA-256 of a secret, hex-encoded — how a presented link is looked up.
    /// </summary>
    /// <remarks>
    /// A plain hash rather than a password hash on purpose: the secret is 256 random bits, so there
    /// is no dictionary to run against it, and a link has to be checked on every page load.
    /// </remarks>
    public static string HashOf(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret.Trim())));
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    /// <summary>The order this link opens, and the only one it can.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>SHA-256 of the secret, hex. Unique, so one secret opens one booking.</summary>
    public string TokenHash { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the link was last opened. For support: "did they ever get it?"</summary>
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Set when the traveller, or the agency, retires a link early.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this link still opens its booking at <paramref name="now"/>.</summary>
    public bool IsUsableAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>Notes that somebody opened the booking with this link.</summary>
    public void RecordUse(DateTimeOffset now) => LastUsedAt = now;

    /// <summary>Retires the link. A fresh one has to be issued to get back in.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    /// <summary>Base64 without padding or URL-unsafe characters, so it survives an email and a URL.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
