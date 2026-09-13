using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Application.Documents;

/// <summary>A time-limited link to one document's PDF, and when it stops working.</summary>
public sealed record SignedDocumentDownload(string Path, DateTimeOffset ExpiresAt);

/// <summary>
/// The two links to an issued document's PDF, and the checks that make each of them a credential.
/// </summary>
/// <remarks>
/// <para>
/// A PDF opened in a new tab arrives with no <c>Authorization</c> header, so the link itself has to
/// prove its holder may have the file — the same reasoning as <c>KybDocumentLink</c>. Both links are
/// signatures made with the server's own key (<see cref="ITokenHasher"/>), so nothing is stored that
/// a leaked database could replay.
/// </para>
/// <list type="bullet">
/// <item><b>The agent's download link</b> expires after <see cref="DownloadLifetime"/>. It sits on a
/// page in the console, and a page is reloaded far more often than an hour.</item>
/// <item><b>The customer's link</b> expires with the booking link that revealed it. The
/// manage-my-booking page mints one every time a traveller opens it, and it is a bearer token like
/// the page's own link: one that never expires goes on working after the traveller's mailbox has
/// changed hands and after the agency is gone (issue 174). A link minted before that carries no
/// deadline and is refused. Rotating <c>Security:TokenHashKey</c> revokes every one of them at
/// once.</item>
/// </list>
/// <para>
/// Base64url, so a signature can sit in a path without escaping.
/// </para>
/// </remarks>
public sealed class DocumentLinks
{
    /// <summary>How long an agent's download link works.</summary>
    public static readonly TimeSpan DownloadLifetime = TimeSpan.FromHours(1);

    private readonly ITokenHasher _tokenHasher;
    private readonly TimeProvider _clock;

    public DocumentLinks(ITokenHasher tokenHasher, TimeProvider clock)
    {
        _tokenHasher = tokenHasher;
        _clock = clock;
    }

    /// <summary>A signed, expiring path to <paramref name="documentId"/>'s PDF, for the console.</summary>
    public SignedDocumentDownload DownloadFor(Guid documentId)
    {
        var expiresAt = _clock.GetUtcNow().Add(DownloadLifetime);
        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return new SignedDocumentDownload(
            $"/api/v1/documents/{documentId}/pdf?expires={expires}&signature={Sign(DownloadPayload(documentId, expires))}",
            expiresAt);
    }

    /// <summary>True when the signature matches <paramref name="documentId"/> and the link has not expired.</summary>
    public bool IsValidDownload(Guid documentId, long expires, string? signature) =>
        !HasPassed(expires)
        && Matches(Sign(DownloadPayload(documentId, expires.ToString(CultureInfo.InvariantCulture))), signature);

    /// <summary>
    /// The path a customer downloads <paramref name="documentId"/> from, working until
    /// <paramref name="expiresAt"/>.
    /// </summary>
    /// <remarks>
    /// The caller chooses the deadline, and the manage-my-booking page passes the expiry of the link
    /// the traveller opened it with — so a document link lapses exactly when the link that revealed
    /// it does (issue 174).
    /// </remarks>
    public string PublicPathFor(Guid documentId, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return $"/api/v1/public/documents/{documentId}/{Sign(PublicPayload(documentId, expires))}?expires={expires}";
    }

    /// <summary>
    /// True when <paramref name="token"/> is the customer link's signature for
    /// <paramref name="documentId"/> at <paramref name="expires"/>, and that deadline has not passed.
    /// </summary>
    /// <remarks>
    /// A link carrying no deadline is one minted before issue 174, when these were permanent. It is
    /// refused like any other lapsed link: they were only ever shown on the manage-my-booking page,
    /// which mints a fresh one every time it is opened.
    /// </remarks>
    public bool IsValidPublic(Guid documentId, long? expires, string? token) =>
        expires is { } deadline
        && !HasPassed(deadline)
        && Matches(Sign(PublicPayload(documentId, deadline.ToString(CultureInfo.InvariantCulture))), token);

    // The two payloads differ, so a customer's token can never pass as an agent's link, and each
    // names its own deadline, so moving one breaks the signature it came with.
    private static string DownloadPayload(Guid documentId, string expires) => $"generated-document-download:{documentId:N}:{expires}";

    private static string PublicPayload(Guid documentId, string expires) => $"generated-document-customer:{documentId:N}:{expires}";

    /// <summary>
    /// True when a deadline in Unix seconds has passed.
    /// </summary>
    /// <remarks>
    /// Compared as numbers rather than through <see cref="DateTimeOffset.FromUnixTimeSeconds"/>,
    /// which throws for a value outside the range it can represent — and this one comes out of a URL
    /// that anyone can type.
    /// </remarks>
    private bool HasPassed(long expires) => expires <= _clock.GetUtcNow().ToUnixTimeSeconds();

    private string Sign(string payload) =>
        _tokenHasher.Hash(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Fixed-time: a byte-by-byte comparison leaks how much of a signature matched, which is enough
    /// to forge one a character at a time.
    /// </summary>
    private static bool Matches(string expected, string? presented) =>
        !string.IsNullOrWhiteSpace(presented)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
