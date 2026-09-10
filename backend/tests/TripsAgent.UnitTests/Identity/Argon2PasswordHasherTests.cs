using FluentAssertions;
using TripsAgent.Infrastructure.Identity;

namespace TripsAgent.UnitTests.Identity;

public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void A_hash_never_contains_the_password()
    {
        var hash = _hasher.Hash("CorrectHorse1");

        hash.Should().NotContain("CorrectHorse1");
        hash.Should().StartWith("argon2id$");
    }

    [Fact]
    public void The_right_password_verifies()
    {
        var hash = _hasher.Hash("CorrectHorse1");

        _hasher.Verify("CorrectHorse1", hash).Verified.Should().BeTrue();
    }

    [Theory]
    [InlineData("correcthorse1")]    // case matters
    [InlineData("CorrectHorse")]     // one character short
    [InlineData("CorrectHorse1 ")]   // trailing space is a different password
    [InlineData("")]
    public void The_wrong_password_does_not_verify(string attempt)
    {
        var hash = _hasher.Hash("CorrectHorse1");

        _hasher.Verify(attempt, hash).Verified.Should().BeFalse();
    }

    [Fact]
    public void Hashing_the_same_password_twice_gives_different_hashes()
    {
        // A fresh random salt per hash. Without one, two users sharing a password share a hash,
        // and cracking one cracks both.
        _hasher.Hash("CorrectHorse1").Should().NotBe(_hasher.Hash("CorrectHorse1"));
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("argon2id$bad$format")]
    [InlineData("argon2id$19456$2$1$!!!notbase64!!!$AAAA")]
    [InlineData("bcrypt$19456$2$1$AAAA$AAAA")]
    public void A_malformed_hash_fails_verification_rather_than_throwing(string hash)
    {
        // A corrupt row must not become a way to crash the sign-in endpoint.
        var act = () => _hasher.Verify("CorrectHorse1", hash);

        act.Should().NotThrow();
        act().Verified.Should().BeFalse();
    }

    [Fact]
    public void A_current_hash_does_not_need_rehashing()
    {
        var hash = _hasher.Hash("CorrectHorse1");

        _hasher.Verify("CorrectHorse1", hash).NeedsRehash.Should().BeFalse();
    }

    [Fact]
    public void A_hash_made_with_weaker_parameters_is_flagged_for_rehashing()
    {
        // Simulates a hash stored before the memory cost was raised: same format, lower cost.
        var current = _hasher.Hash("CorrectHorse1").Split('$');
        current[1] = "8192";

        // Recompute with the weaker parameters so the password genuinely verifies against it.
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(
            System.Text.Encoding.UTF8.GetBytes("CorrectHorse1"))
        {
            Salt = Convert.FromBase64String(current[4]),
            MemorySize = 8192,
            Iterations = 2,
            DegreeOfParallelism = 1,
        };
        current[5] = Convert.ToBase64String(argon2.GetBytes(32));

        var result = _hasher.Verify("CorrectHorse1", string.Join('$', current));

        // Still accepted — nobody gets locked out by a parameter change — but flagged, so the
        // sign-in path can quietly upgrade it while it has the plaintext in hand.
        result.Verified.Should().BeTrue();
        result.NeedsRehash.Should().BeTrue();
    }
}
