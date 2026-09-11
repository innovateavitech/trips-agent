using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>A time-limited link to one document, and when it stops working.</summary>
public sealed record SignedDocumentLink(string Path, DateTimeOffset ExpiresAt);

/// <summary>
/// Signs and validates short-lived links to KYB documents.
/// </summary>
/// <remarks>
/// <para>
/// A reviewer opens a document in a new tab, and a new tab does not carry an <c>Authorization</c>
/// header — so the link itself has to be the credential. It is signed with the same server-side
/// key as every other generated secret, scoped to one document id, and expires in minutes.
/// </para>
/// <para>
/// This is the local equivalent of an object-store presigned URL. When the asset pipeline (#18)
/// brings real presigned URLs, this is replaced by them rather than living alongside — the shape
/// of the call is deliberately the same.
/// </para>
/// </remarks>
public sealed class KybDocumentLink
{
    /// <summary>Long enough to open and read a document, short enough that a leaked link is stale.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITokenHasher _tokenHasher;
    private readonly TimeProvider _clock;

    public KybDocumentLink(ITokenHasher tokenHasher, TimeProvider clock)
    {
        _tokenHasher = tokenHasher;
        _clock = clock;
    }

    /// <summary>Builds a signed, time-limited path to <paramref name="documentId"/>.</summary>
    public SignedDocumentLink Create(Guid documentId)
    {
        var expiresAt = _clock.GetUtcNow().Add(Lifetime);
        var expires = expiresAt.ToUnixTimeSeconds();
        var signature = Sign(documentId, expires);

        return new SignedDocumentLink(
            $"/api/v1/admin/kyb/documents/{documentId}?expires={expires.ToString(CultureInfo.InvariantCulture)}&signature={Uri.EscapeDataString(signature)}",
            expiresAt);
    }

    /// <summary>True when the signature matches and the link has not expired.</summary>
    public bool IsValid(Guid documentId, long expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= _clock.GetUtcNow())
        {
            return false;
        }

        // Fixed-time comparison: a byte-by-byte one leaks how much of the signature matched,
        // which is enough to forge one a character at a time.
        var expected = Encoding.UTF8.GetBytes(Sign(documentId, expires));
        var presented = Encoding.UTF8.GetBytes(signature);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// The expiry is inside the signed payload, so moving the deadline invalidates the signature
    /// rather than extending the link.
    /// </summary>
    private string Sign(Guid documentId, long expires) =>
        _tokenHasher.Hash($"kyb-document:{documentId:N}:{expires.ToString(CultureInfo.InvariantCulture)}");
}
