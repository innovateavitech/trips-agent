using System.Globalization;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TripsAgent.Application.Identity;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Infrastructure.Identity;

/// <summary>How access tokens are signed and how long they last.</summary>
public sealed class JwtOptions
{
    /// <summary>Minimum bytes for the signing key. HS256 keys shorter than this are rejected.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; init; } = "https://tripsagent.local";

    public string Audience { get; init; } = "trips-agent-api";

    /// <summary>The HMAC signing key, base64-encoded. Never committed for a real environment.</summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>
    /// Fifteen minutes. Short on purpose: an access token cannot be revoked, so the only thing
    /// limiting the damage of a stolen one is how quickly it expires. The refresh token — which
    /// <i>can</i> be revoked — is what keeps people signed in.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Decodes and validates the signing key.</summary>
    public byte[] SigningKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            throw new InvalidOperationException(
                """
                No JWT signing key configured at Jwt:SigningKey.

                Generate one with:  openssl rand -base64 32
                then set it as the environment variable Jwt__SigningKey.

                Never commit a production key. Rotating it signs everyone out, which is the
                point if it leaks.
                """);
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Jwt:SigningKey is not valid base64.", ex);
        }

        return key.Length >= MinimumSigningKeyBytes
            ? key
            : throw new InvalidOperationException(
                $"Jwt:SigningKey must decode to at least {MinimumSigningKeyBytes} bytes; it is {key.Length}. "
                + "HS256 with a short key is trivially brute-forced.");
    }
}

/// <summary>Signs access tokens with HMAC-SHA256.</summary>
/// <remarks>
/// Symmetric signing, because the only thing validating these tokens is the same API that issues
/// them. Asymmetric keys earn their extra moving parts when a third party has to verify without
/// being able to mint — worth revisiting if the storefront or a partner ever does.
/// </remarks>
public sealed class JwtAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _credentials;

    public JwtAccessTokenIssuer(JwtOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _clock = clock;
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(options.SigningKeyBytes()),
            SecurityAlgorithms.HmacSha256);
    }

    public AccessToken Issue(User user, IReadOnlyCollection<string> roles, Guid? rootAgencyId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        var now = _clock.GetUtcNow();
        var expiresAt = now.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(TripsClaimTypes.Subject, user.Id.ToString()),
            new(TripsClaimTypes.Email, user.Email),
        };

        // Platform staff have no agency, and the absence of the claim is what tells the
        // middleware to treat them as such — never a Guid.Empty placeholder, which would look
        // like a real tenant that matches no rows.
        if (user.AgencyId is { } agencyId)
        {
            claims.Add(new Claim(TripsClaimTypes.AgencyId, agencyId.ToString()));
            claims.Add(new Claim(TripsClaimTypes.RootAgencyId, (rootAgencyId ?? agencyId).ToString()));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(TripsClaimTypes.Role, role));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        };

        // A JWT id, so a specific token can be identified in a log without the token itself
        // appearing there.
        descriptor.Claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }

    /// <summary>The token lifetime, exposed so a response can tell the client when to refresh.</summary>
    public int AccessTokenSeconds =>
        (int)Math.Round(_options.AccessTokenLifetime.TotalSeconds, MidpointRounding.AwayFromZero);

    /// <summary>Formats the lifetime for an OAuth-style <c>expires_in</c> field.</summary>
    public string AccessTokenSecondsText =>
        AccessTokenSeconds.ToString(CultureInfo.InvariantCulture);
}
