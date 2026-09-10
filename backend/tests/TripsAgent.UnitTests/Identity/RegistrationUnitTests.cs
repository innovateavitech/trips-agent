using FluentAssertions;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Domain.Identity;
using TripsAgent.Infrastructure.Identity;

namespace TripsAgent.UnitTests.Identity;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("password1")]
    [InlineData("12345678")]        // all digits is allowed — length is what resists guessing
    [InlineData("correct horse battery staple 9")]
    public void A_password_of_eight_or_more_with_a_number_is_accepted(string password) =>
        PasswordPolicy.IsValid(password).Should().BeTrue();

    [Fact]
    public void Seven_characters_is_too_short() =>
        PasswordPolicy.Validate("passwo1").Should().ContainSingle()
            .Which.Should().Contain("at least 8");

    [Fact]
    public void A_password_with_no_number_is_rejected() =>
        PasswordPolicy.Validate("password").Should().ContainSingle()
            .Which.Should().Contain("number");

    [Fact]
    public void Every_broken_rule_is_reported_at_once()
    {
        // So a form can show both problems together, rather than fix-one-resubmit-find-the-next.
        PasswordPolicy.Validate("pass").Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_password_breaks_both_rules(string? password) =>
        PasswordPolicy.Validate(password).Should().HaveCount(2);

    [Fact]
    public void An_absurdly_long_password_is_rejected()
    {
        // Argon2id is deliberately expensive; hashing a megabyte on every attempt would be a
        // cheap way to tie up the server.
        PasswordPolicy.IsValid(new string('a', 200) + "1").Should().BeFalse();
    }

    [Fact]
    public void Non_ascii_digits_do_not_count_as_the_number()
    {
        // Arabic-Indic "١" is a digit to char.IsDigit but not what the FRD means by a number.
        PasswordPolicy.IsValid("password١").Should().BeFalse();
    }
}

public class HmacTokenHasherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void The_same_token_always_hashes_the_same_way()
    {
        // Deterministic on purpose: verification looks the stored hash up by recomputing it.
        var hasher = new HmacTokenHasher(Key);

        hasher.Hash("048213").Should().Be(hasher.Hash("048213"));
    }

    [Fact]
    public void A_different_key_gives_a_different_hash()
    {
        // The property that makes a leaked database useless on its own: without the key, the
        // stored value cannot be matched against all million six-digit codes.
        var other = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        new HmacTokenHasher(Key).Hash("048213").Should().NotBe(new HmacTokenHasher(other).Hash("048213"));
    }

    [Fact]
    public void The_hash_does_not_contain_the_token() =>
        new HmacTokenHasher(Key).Hash("048213").Should().NotContain("048213");

    [Fact]
    public void A_key_shorter_than_256_bits_is_refused()
    {
        var act = () => new HmacTokenHasher(new byte[16]);

        act.Should().Throw<ArgumentException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void Numeric_codes_keep_their_leading_zeros()
    {
        var hasher = new HmacTokenHasher(Key);

        var codes = Enumerable.Range(0, 2000).Select(_ => hasher.GenerateNumericCode(6)).ToList();

        // Across 2000 draws some will start with zero; every one must still be six digits.
        codes.Should().OnlyContain(code => code.Length == 6 && code.All(char.IsAsciiDigit));
        codes.Should().Contain(code => code.StartsWith('0'));
    }

    [Fact]
    public void Numeric_codes_are_not_repeated_in_a_small_sample()
    {
        var hasher = new HmacTokenHasher(Key);

        var codes = Enumerable.Range(0, 50).Select(_ => hasher.GenerateNumericCode(6)).ToList();

        // Not proof of randomness, but it catches the embarrassing failure: a fixed or
        // counter-based generator.
        codes.Distinct().Count().Should().BeGreaterThan(45);
    }

    [Fact]
    public void Opaque_tokens_are_url_safe()
    {
        var token = new HmacTokenHasher(Key).GenerateOpaqueToken();

        token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        token.Length.Should().BeGreaterThanOrEqualTo(43, "256 bits in base64url");
    }
}

public class VerificationEmailTests
{
    [Fact]
    public void The_code_appears_in_both_the_subject_and_the_body()
    {
        var email = VerificationEmail.Create("ada@example.com", "Ada", "048213", TimeSpan.FromMinutes(15));

        email.Subject.Should().Contain("048213");
        email.HtmlBody.Should().Contain("048213");
        email.TextBody.Should().Contain("048213");
        email.TextBody.Should().Contain("15 minutes");
    }

    [Fact]
    public void A_name_containing_markup_is_escaped_in_the_html()
    {
        // Anything a person typed must arrive as text. Otherwise a signup with a crafted first
        // name puts a working link inside an email that looks like it came from us.
        var email = VerificationEmail.Create(
            "ada@example.com", "<a href=\"https://evil.example\">Ada</a>", "048213", TimeSpan.FromMinutes(15));

        email.HtmlBody.Should().NotContain("<a href=");
        email.HtmlBody.Should().Contain("&lt;a href=");
    }

    [Fact]
    public void There_is_always_a_plain_text_part() =>
        VerificationEmail.Create("ada@example.com", "Ada", "048213", TimeSpan.FromMinutes(15))
            .TextBody.Should().NotBeNullOrWhiteSpace();
}
