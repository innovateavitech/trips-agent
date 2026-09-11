using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TripsAgent.Infrastructure.Security;

namespace TripsAgent.UnitTests.Security;

/// <summary>
/// The encryption behind "merchant_key encrypted at rest". Each test is a way the stored value could
/// fail to protect the key — or fail to give it back.
/// </summary>
public class AesGcmSecretProtectorTests
{
    private const string Purpose = "supplier_credentials.merchant_key";
    // Any value will do: these tests are about what the protector does to a plaintext, not what
    // the plaintext is. Named for that, and deliberately not shaped like a credential, so the
    // secret scanner stays quiet for the right reason rather than being told to look away.
    private const string Plaintext = "sample-plaintext-0001";

    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes));

    [Fact]
    public void A_protected_secret_comes_back_unchanged()
    {
        var stored = _protector.Protect(Plaintext, Purpose);

        _protector.Unprotect(stored, Purpose).Should().Be(Plaintext);
    }

    [Fact]
    public void The_stored_bytes_do_not_contain_the_secret()
    {
        var stored = _protector.Protect(Plaintext, Purpose);

        var asText = Encoding.UTF8.GetString(stored);
        asText.Should().NotContain(Plaintext);
        Convert.ToBase64String(stored).Should().NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Plaintext)));
    }

    [Fact]
    public void Protecting_the_same_secret_twice_gives_different_bytes()
    {
        // A fixed nonce would make equal keys produce equal ciphertext — telling anyone with the table
        // which agencies share a merchant key — and, for GCM, would break the cipher outright.
        var first = _protector.Protect(Plaintext, Purpose);
        var second = _protector.Protect(Plaintext, Purpose);

        first.Should().NotEqual(second);
    }

    [Fact]
    public void A_value_made_for_one_column_does_not_decrypt_for_another()
    {
        var stored = _protector.Protect(Plaintext, Purpose);

        var wrongPurpose = () => _protector.Unprotect(stored, "supplier_credentials.bearer_token");

        wrongPurpose.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void An_altered_value_fails_rather_than_decrypting_to_something_else()
    {
        var stored = _protector.Protect(Plaintext, Purpose);
        stored[^1] ^= 0x01;

        var tampered = () => _protector.Unprotect(stored, Purpose);

        tampered.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_different_key_cannot_read_it()
    {
        var stored = _protector.Protect(Plaintext, Purpose);
        var other = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes));

        var read = () => other.Unprotect(stored, Purpose);

        read.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(28)]
    public void Something_that_was_never_protected_is_refused(int length)
    {
        var read = () => _protector.Unprotect(new byte[length], Purpose);

        read.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void Only_a_256_bit_key_is_accepted(int bytes)
    {
        var build = () => new AesGcmSecretProtector(new byte[bytes]);

        build.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }
}
