using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Security;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>Settings for supplier integration that are not per-supplier.</summary>
public sealed class SupplierCredentialOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Suppliers";

    /// <summary>
    /// Which credential an agency's calls use. Defaults to the platform's — see
    /// <see cref="SupplierCredentialResolution.PlatformOnly"/> for why that is the conservative choice
    /// while open question 1 is unanswered.
    /// </summary>
    public SupplierCredentialResolution CredentialResolution { get; set; } = SupplierCredentialResolution.PlatformOnly;
}

/// <summary>
/// <see cref="ISupplierCredentialStore"/> over <c>supplier.supplier_credentials</c>, encrypting with
/// <see cref="ISecretProtector"/>.
/// </summary>
/// <remarks>
/// <para>
/// Plaintext exists in exactly two places: the arguments to <see cref="SaveAsync"/>, which are
/// encrypted before anything is tracked, and the <see cref="SupplierCredentials"/> that
/// <see cref="FindAsync"/> returns. The entity only ever holds ciphertext, so the audit interceptor,
/// EF's change tracker and any serialiser that meets it see bytes that are useless without the key.
/// </para>
/// <para>
/// Nothing here logs. A failure to decrypt says which credential failed, never what it contained.
/// </para>
/// <para>
/// Writing the <b>platform's</b> credential (a null agency) needs an open platform scope: row-level
/// security only lets an agency write its own rows. That is deliberate — replacing the platform's
/// merchant key is a platform operation, and the scope records who did it and why.
/// </para>
/// </remarks>
public sealed class SupplierCredentialStore : ISupplierCredentialStore
{
    /// <summary>The purpose the merchant key is encrypted under. Changing it orphans every stored key.</summary>
    public const string MerchantKeyPurpose = "supplier_credentials.merchant_key";

    /// <summary>The purpose the bearer token is encrypted under.</summary>
    public const string BearerTokenPurpose = "supplier_credentials.bearer_token";

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;
    private readonly SupplierCredentialOptions _options;

    public SupplierCredentialStore(AppDbContext db, ISecretProtector protector, TimeProvider clock, SupplierCredentialOptions options)
    {
        _db = db;
        _protector = protector;
        _clock = clock;
        _options = options;
    }

    public async Task<SupplierCredentials?> FindAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierCode);

        var supplierId = await SupplierIdAsync(supplierCode, cancellationToken);

        if (supplierId is null)
        {
            return null;
        }

        var wantsAgencyOwn = _options.CredentialResolution == SupplierCredentialResolution.AgencyFirst && agencyId is not null;

        // The platform's credential always, and the agency's own only when the setting allows it.
        var candidates = await _db.SupplierCredentials
            .Where(credential => credential.SupplierId == supplierId && credential.Environment == environment)
            .Where(credential => credential.AgencyId == null || (wantsAgencyOwn && credential.AgencyId == agencyId))
            .ToListAsync(cancellationToken);

        var chosen = (wantsAgencyOwn ? candidates.Find(credential => credential.AgencyId == agencyId) : null)
                     ?? candidates.Find(credential => credential.AgencyId is null);

        return chosen is null ? null : Decrypt(chosen, supplierCode);
    }

    public async Task<Guid> SaveAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        string merchantCode,
        string merchantKey,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantKey);

        var supplierId = await SupplierIdAsync(supplierCode, cancellationToken)
            ?? throw new InvalidOperationException($"No supplier with code '{supplierCode}' is registered.");

        var keyEncrypted = _protector.Protect(merchantKey, MerchantKeyPurpose);
        var tokenEncrypted = string.IsNullOrEmpty(bearerToken) ? null : _protector.Protect(bearerToken, BearerTokenPurpose);
        var now = _clock.GetUtcNow();

        var existing = await _db.SupplierCredentials.SingleOrDefaultAsync(
            credential => credential.SupplierId == supplierId
                          && credential.Environment == environment
                          && credential.AgencyId == agencyId,
            cancellationToken);

        if (existing is null)
        {
            existing = SupplierCredential.Create(supplierId, agencyId, environment, merchantCode, keyEncrypted, tokenEncrypted, now);
            _db.SupplierCredentials.Add(existing);
        }
        else
        {
            existing.Rotate(merchantCode, keyEncrypted, tokenEncrypted, now);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return existing.Id;
    }

    private Task<Guid?> SupplierIdAsync(string supplierCode, CancellationToken cancellationToken) =>
        _db.Suppliers
            .Where(supplier => supplier.Code == supplierCode)
            .Select(supplier => (Guid?)supplier.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private SupplierCredentials Decrypt(SupplierCredential credential, string supplierCode)
    {
        try
        {
            return new SupplierCredentials(
                credential.Id,
                supplierCode,
                credential.Environment,
                credential.AgencyId,
                credential.MerchantCode,
                _protector.Unprotect(credential.MerchantKeyEncrypted, MerchantKeyPurpose),
                credential.BearerTokenEncrypted is null
                    ? null
                    : _protector.Unprotect(credential.BearerTokenEncrypted, BearerTokenPurpose));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"{credential} could not be decrypted. Either Security:SecretEncryptionKey is not the key it was "
                + "stored under, or the stored value was altered. Re-enter the credential rather than guessing.",
                ex);
        }
    }
}
