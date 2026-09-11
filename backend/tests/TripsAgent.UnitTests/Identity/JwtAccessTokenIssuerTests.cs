using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using TripsAgent.Application.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.Infrastructure.Identity;

namespace TripsAgent.UnitTests.Identity;

public class JwtAccessTokenIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Key =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private static JwtAccessTokenIssuer NewIssuer(TimeSpan? lifetime = null) =>
        new(new JwtOptions
        {
            Issuer = "https://tripsagent.test",
            Audience = "trips-agent-api",
            SigningKey = Key,
            AccessTokenLifetime = lifetime ?? TimeSpan.FromMinutes(15),
        },
        new ManualClock(Now));

    private static JwtSecurityToken Decode(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void An_agency_users_token_carries_the_claims_the_tenant_context_reads()
    {
        var agencyId = Guid.CreateVersion7();
        var rootAgencyId = Guid.CreateVersion7();
        var user = User.ForAgency(agencyId, "ada@example.com", "hash", "Ada", "O");

        var token = Decode(NewIssuer().Issue(user, ["Owner", "Agent"], [], rootAgencyId).Value);

        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.Subject && c.Value == user.Id.ToString());
        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.AgencyId && c.Value == agencyId.ToString());
        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.RootAgencyId && c.Value == rootAgencyId.ToString());
        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.Email && c.Value == "ada@example.com");

        token.Claims.Where(c => c.Type == TripsClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["Owner", "Agent"]);
    }

    [Fact]
    public void A_principals_root_defaults_to_itself()
    {
        var agencyId = Guid.CreateVersion7();
        var user = User.ForAgency(agencyId, "ada@example.com", "hash", "Ada", "O");

        var token = Decode(NewIssuer().Issue(user, [], [], rootAgencyId: null).Value);

        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.RootAgencyId && c.Value == agencyId.ToString());
    }

    [Fact]
    public void Platform_staff_get_no_agency_claim_at_all()
    {
        var user = User.ForPlatform("admin@tripsagent.test", "hash", "Ada", "O");

        var token = Decode(NewIssuer().Issue(user, ["Super Admin"], [], null).Value);

        // Not an empty Guid: that would look like a real tenant, and match no rows.
        token.Claims.Should().NotContain(c => c.Type == TripsClaimTypes.AgencyId);
        token.Claims.Should().NotContain(c => c.Type == TripsClaimTypes.RootAgencyId);
        token.Claims.Should().Contain(c => c.Type == TripsClaimTypes.Subject);
    }

    [Fact]
    public void The_token_expires_after_fifteen_minutes()
    {
        var user = User.ForPlatform("admin@tripsagent.test", "hash", "Ada", "O");

        var issued = NewIssuer().Issue(user, [], [], null);

        issued.ExpiresAt.Should().Be(Now.AddMinutes(15));
        Decode(issued.Value).ValidTo.Should().BeCloseTo(Now.AddMinutes(15).UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_issuer_and_audience_are_stamped_on_the_token()
    {
        var user = User.ForPlatform("admin@tripsagent.test", "hash", "Ada", "O");

        var token = Decode(NewIssuer().Issue(user, [], [], null).Value);

        token.Issuer.Should().Be("https://tripsagent.test");
        token.Audiences.Should().Contain("trips-agent-api");
    }

    [Fact]
    public void Every_token_gets_its_own_id()
    {
        var user = User.ForPlatform("admin@tripsagent.test", "hash", "Ada", "O");
        var issuer = NewIssuer();

        var first = Decode(issuer.Issue(user, [], [], null).Value).Id;
        var second = Decode(issuer.Issue(user, [], [], null).Value).Id;

        // So one token can be named in a log without the token itself appearing there.
        first.Should().NotBeNullOrEmpty();
        first.Should().NotBe(second);
    }

    [Fact]
    public void The_password_hash_never_reaches_the_token()
    {
        var user = User.ForAgency(Guid.CreateVersion7(), "ada@example.com", "argon2id$secret-hash", "Ada", "O");

        // A JWT is signed, not encrypted — anyone holding it can read every claim.
        NewIssuer().Issue(user, [], [], null).Value.Should().NotContain("secret-hash");
    }

    [Fact]
    public void A_missing_signing_key_fails_with_instructions()
    {
        var act = () => new JwtAccessTokenIssuer(new JwtOptions { SigningKey = "" }, new ManualClock(Now));

        act.Should().Throw<InvalidOperationException>().WithMessage("*openssl rand -base64 32*");
    }

    [Fact]
    public void A_signing_key_shorter_than_256_bits_is_refused()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        var act = () => new JwtAccessTokenIssuer(new JwtOptions { SigningKey = shortKey }, new ManualClock(Now));

        // HS256 with a short key is brute-forceable, and a forged token is a full account takeover.
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void A_signing_key_that_is_not_base64_is_refused()
    {
        var act = () => new JwtAccessTokenIssuer(new JwtOptions { SigningKey = "not base64!!" }, new ManualClock(Now));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid base64*");
    }
}

/// <summary>A clock a test sets by hand, so token lifetimes are asserted exactly.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
