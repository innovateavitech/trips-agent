using System.Security.Cryptography;
using TripsAgent.Infrastructure.Security;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// The key ring every context the fixture builds encrypts with (issue 104). Random for each test run,
/// so no key is ever written down, and one for the whole run, so every context shares one EF model.
/// </summary>
public static class TestFieldEncryption
{
    public const string KeyId = "test_key";

    public static AesGcmFieldEncryptor Encryptor { get; } =
        new(KeyId, new Dictionary<string, byte[]> { [KeyId] = RandomNumberGenerator.GetBytes(AesGcmFieldEncryptor.KeyBytes) });
}
