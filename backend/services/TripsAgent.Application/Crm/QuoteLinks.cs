using System.Buffers.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Application.Crm;

/// <summary>
/// The link a customer opens their quote with: how one is made, and what is kept of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a keyed hash is stored</b> (issue 175). Whoever holds the link can read and answer the
/// quote, which makes it a bearer token — so the row keeps <c>HMAC(key, token)</c> and never the token
/// itself, exactly as refresh tokens, reset tokens and booking links do. A leaked database backup is
/// then a list of hashes rather than a set of live links.
/// </para>
/// <para>
/// <b>The token is a signature rather than a stored secret.</b> It is the keyed hash of this quote's
/// id, base64url — the shape <see cref="Documents.DocumentLinks"/> already uses for a traveller's
/// voucher link. That is what lets the agent's console show the link again whenever they open the
/// quote, while the database holds nothing that could be replayed. The price is that the key <i>and</i>
/// a quote id would together mint a link, so <c>Security:TokenHashKey</c> is a secret of the same
/// class as a signing key, and rotating it retires every quote link at once.
/// </para>
/// <para>
/// Links made before this existed were 256 random bits. The backfill hashed them, so they go on
/// working — but they cannot be recomputed, so the console shows no link for a quote sent back then.
/// </para>
/// </remarks>
public sealed class QuoteLinks
{
    /// <summary>What the signature is over, so a token of ours can never be read as anything else.</summary>
    private const string Purpose = "crm.quote-link:";

    private readonly ITokenHasher _tokens;

    public QuoteLinks(ITokenHasher tokens) => _tokens = tokens;

    /// <summary>The token in this quote's link. The same quote always has the same one.</summary>
    public string TokenFor(Guid quoteId) =>
        Base64Url.EncodeToString(Convert.FromBase64String(_tokens.Hash($"{Purpose}{quoteId:N}")));

    /// <summary>What is stored, and what a presented link is looked up by: the keyed hash of the token.</summary>
    public string HashOf(string token) => _tokens.Hash(token);

    /// <summary>
    /// The token for <paramref name="quoteId"/> when <paramref name="storedHash"/> is the hash of it,
    /// and null when it is not: a quote sent before the tokens were derived, whose link still opens it
    /// but cannot be shown a second time.
    /// </summary>
    public string? ShowableTokenFor(Guid quoteId, string? storedHash)
    {
        if (storedHash is null)
        {
            return null;
        }

        var token = TokenFor(quoteId);

        return string.Equals(HashOf(token), storedHash, StringComparison.Ordinal) ? token : null;
    }
}
