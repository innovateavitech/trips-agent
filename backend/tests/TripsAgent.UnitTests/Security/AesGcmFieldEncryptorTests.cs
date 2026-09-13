using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TripsAgent.Infrastructure.Persistence.Encryption;
using TripsAgent.Infrastructure.Security;

namespace TripsAgent.UnitTests.Security;

/// <summary>
/// The encryption behind "a passport number is never stored in clear" (issue 104). Each test is a way
/// the stored value could fail to protect the number — or fail to give it back after a key rotation.
/// </summary>
public class AesGcmFieldEncryptorTests
{
    private const string Purpose = EncryptedColumns.OrderTravellerPassportNumber;

    // Shaped like a passport number, and not one: these tests are about what happens to a value, not
    // about what the value is.
    private const string Plaintext = "A01234567";

    private const string ActiveKey = "key2026_09";
    private const string RetiredKey = "key2025_01";

    private readonly AesGcmFieldEncryptor _encryptor = new(ActiveKey, Ring(ActiveKey));

    [Fact]
    public void An_encrypted_value_comes_back_unchanged()
    {
        var stored = _encryptor.Encrypt(Plaintext, Purpose);

        _encryptor.Decrypt(stored, Purpose).Should().Be(Plaintext);
    }

    [Fact]
    public void The_stored_bytes_do_not_contain_the_number()
    {
        var stored = _encryptor.Encrypt(Plaintext, Purpose);

        Encoding.UTF8.GetString(stored).Should().NotContain(Plaintext);
        Convert.ToBase64String(stored).Should().NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Plaintext)));
    }

    [Fact]
    public void Encrypting_the_same_number_twice_gives_different_bytes()
    {
        // Which is what makes these columns unsearchable, deliberately: deterministic ciphertext would
        // let anyone holding the table see which travellers share a passport number.
        _encryptor.Encrypt(Plaintext, Purpose).Should().NotEqual(_encryptor.Encrypt(Plaintext, Purpose));
    }

    [Fact]
    public void Every_value_names_the_key_that_made_it()
    {
        _encryptor.KeyIdOf(_encryptor.Encrypt(Plaintext, Purpose)).Should().Be(ActiveKey);
    }

    [Fact]
    public void A_value_made_for_one_column_does_not_decrypt_for_another()
    {
        var stored = _encryptor.Encrypt(Plaintext, Purpose);

        var wrongColumn = () => _encryptor.Decrypt(stored, EncryptedColumns.AgencyBankAccountNumber);

        wrongColumn.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void An_altered_value_fails_rather_than_decrypting_to_something_else()
    {
        var stored = _encryptor.Encrypt(Plaintext, Purpose);
        stored[^1] ^= 0xFF;

        var tampered = () => _encryptor.Decrypt(stored, Purpose);

        tampered.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_value_under_a_retired_key_still_decrypts_and_is_written_again_under_the_active_one()
    {
        // The rotation path: both keys are held, only the new one encrypts.
        var retired = Ring(RetiredKey);
        var old = new AesGcmFieldEncryptor(RetiredKey, retired);
        var underOldKey = old.Encrypt(Plaintext, Purpose);

        var both = new AesGcmFieldEncryptor(
            ActiveKey,
            new Dictionary<string, byte[]>
            {
                [ActiveKey] = Ring(ActiveKey)[ActiveKey],
                [RetiredKey] = retired[RetiredKey],
            });

        var plaintext = both.Decrypt(underOldKey, Purpose);
        var rewritten = both.Encrypt(plaintext, Purpose);

        plaintext.Should().Be(Plaintext);
        both.KeyIdOf(rewritten).Should().Be(ActiveKey);
    }

    [Fact]
    public void A_value_whose_key_is_no_longer_configured_says_which_key_is_missing()
    {
        var old = new AesGcmFieldEncryptor(RetiredKey, Ring(RetiredKey));
        var underOldKey = old.Encrypt(Plaintext, Purpose);

        var withoutIt = () => _encryptor.Decrypt(underOldKey, Purpose);

        withoutIt.Should().Throw<CryptographicException>().WithMessage($"*{RetiredKey}*");
    }

    [Fact]
    public void A_value_in_the_older_secret_protector_format_is_not_mistaken_for_one_of_these()
    {
        // How passport numbers were stored before this existed. The backfill tells them apart by this.
        var legacy = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes));

        _encryptor.KeyIdOf(legacy.Protect(Plaintext, Purpose)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("key with spaces")]
    [InlineData("key-2026")]
    [InlineData("0123456789012345678901234567890123")]
    public void A_key_id_that_would_not_survive_an_environment_variable_is_refused(string keyId)
    {
        var build = () => new AesGcmFieldEncryptor(
            keyId, new Dictionary<string, byte[]> { [keyId] = RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes) });

        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void A_key_that_is_not_AES_256_is_refused(int bytes)
    {
        var build = () => new AesGcmFieldEncryptor(ActiveKey, new Dictionary<string, byte[]> { [ActiveKey] = new byte[bytes] });

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_active_key_id_with_no_key_behind_it_is_refused()
    {
        var build = () => new AesGcmFieldEncryptor("key2027_01", Ring(ActiveKey));

        build.Should().Throw<ArgumentException>().WithMessage($"*{ActiveKey}*");
    }

    [Fact]
    public void Two_encryptors_with_the_same_keys_share_a_model_and_two_with_different_keys_do_not()
    {
        // EF Core caches one model per key ring, because the converters hold the encryptor.
        var ring = Ring(ActiveKey);
        var same = new AesGcmFieldEncryptor(ActiveKey, ring);
        var another = new AesGcmFieldEncryptor(ActiveKey, ring);
        var different = new AesGcmFieldEncryptor(ActiveKey, Ring(ActiveKey));

        same.Fingerprint.Should().Be(another.Fingerprint);
        same.Fingerprint.Should().NotBe(different.Fingerprint);
    }

    // ------------------------------------------------------------------ from configuration

    [Fact]
    public void With_no_keys_configured_the_columns_refuse_rather_than_storing_anything_in_clear()
    {
        var encryptor = FieldEncryptionConfiguration.Create(new ConfigurationBuilder().Build());

        var encrypt = () => encryptor.Encrypt(Plaintext, Purpose);

        encryptor.Should().BeOfType<UnconfiguredFieldEncryptor>();
        encrypt.Should().Throw<InvalidOperationException>().WithMessage("*Security__FieldEncryption__ActiveKeyId*");
    }

    [Fact]
    public void Configured_keys_are_read_into_a_ring()
    {
        var encryptor = FieldEncryptionConfiguration.Create(Configuration(new Dictionary<string, string?>
        {
            [FieldEncryptionConfiguration.ActiveKeyIdSetting] = ActiveKey,
            [$"{FieldEncryptionConfiguration.KeysSection}:{ActiveKey}"] =
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes)),
        }));

        encryptor.ActiveKeyId.Should().Be(ActiveKey);
        encryptor.Decrypt(encryptor.Encrypt(Plaintext, Purpose), Purpose).Should().Be(Plaintext);
    }

    [Fact]
    public void An_active_key_id_with_no_key_configured_fails_at_startup_rather_than_at_the_first_booking()
    {
        var create = () => FieldEncryptionConfiguration.Create(Configuration(new Dictionary<string, string?>
        {
            [FieldEncryptionConfiguration.ActiveKeyIdSetting] = ActiveKey,
        }));

        create.Should().Throw<InvalidOperationException>().WithMessage($"*{ActiveKey}*");
    }

    [Fact]
    public void A_key_that_is_not_base64_says_so()
    {
        var create = () => FieldEncryptionConfiguration.Create(Configuration(new Dictionary<string, string?>
        {
            [FieldEncryptionConfiguration.ActiveKeyIdSetting] = ActiveKey,
            [$"{FieldEncryptionConfiguration.KeysSection}:{ActiveKey}"] = "not base64!",
        }));

        create.Should().Throw<InvalidOperationException>().WithMessage("*base64*");
    }

    private static Dictionary<string, byte[]> Ring(string keyId) =>
        new() { [keyId] = RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes) };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
