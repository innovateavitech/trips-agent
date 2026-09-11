using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// A supplier credential, decrypted, for the length of one adapter call.
/// </summary>
/// <remarks>
/// <para>
/// The only type in the system that holds a merchant key in clear. It is built by
/// <see cref="ISupplierCredentialStore"/> at the moment an adapter needs it and should go no further
/// than that adapter: never into a DTO, a log scope, an exception message or a cache.
/// </para>
/// <para>
/// <see cref="ToString"/> is overridden because a record's generated one prints every property, and
/// <c>logger.LogInformation("Using {Credentials}", credentials)</c> would otherwise write the key to
/// the logs. The override names the credential without any part of either secret.
/// </para>
/// </remarks>
public sealed record SupplierCredentials(
    Guid CredentialId,
    string SupplierCode,
    SupplierEnvironment Environment,
    Guid? AgencyId,
    string MerchantCode,
    string MerchantKey,
    string? BearerToken)
{
    /// <summary>True when this is the platform's own merchant account rather than an agency's.</summary>
    public bool IsPlatformLevel => AgencyId is null;

    public override string ToString() =>
        $"SupplierCredentials {{ CredentialId = {CredentialId}, SupplierCode = {SupplierCode}, "
        + $"Environment = {Environment}, Owner = {(AgencyId is null ? "platform" : AgencyId.ToString())}, "
        + $"MerchantCode = {MerchantCode}, MerchantKey = [redacted], BearerToken = [redacted] }}";
}

/// <summary>
/// Which credential an agency's supplier calls use — the switch for open question 1.
/// </summary>
/// <remarks>
/// Open question 1 in the delivery plan asks whether each agency holds its own Trips Africa merchant
/// account or the platform transacts as one. The schema supports both (a nullable
/// <c>supplier_credentials.agency_id</c>); this setting chooses between them without a migration.
/// </remarks>
public enum SupplierCredentialResolution
{
    /// <summary>
    /// Every call uses the platform's credential, whoever it is for. The default: it is what the
    /// supplier's API actually offers today (one merchant account, no per-agent provisioning), and it
    /// cannot route an agency's money through an account nobody has confirmed exists.
    /// </summary>
    PlatformOnly = 1,

    /// <summary>
    /// An agency's own credential when it has one, the platform's otherwise. For the answer where
    /// agents hold their own merchant accounts.
    /// </summary>
    AgencyFirst = 2,
}

/// <summary>
/// Reads and writes supplier credentials, encrypting on the way in and decrypting on the way out.
/// </summary>
public interface ISupplierCredentialStore
{
    /// <summary>
    /// The credential an adapter should use for a call made on behalf of <paramref name="agencyId"/>,
    /// decrypted. Null when none is configured.
    /// </summary>
    /// <param name="agencyId">The agency the call is for, or null for platform work.</param>
    public Task<SupplierCredentials?> FindAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a credential, encrypting both secrets. Replaces the existing one for the same supplier,
    /// environment and owner rather than adding a second.
    /// </summary>
    /// <param name="agencyId">The owning agency, or null for the platform's own credential.</param>
    /// <returns>The credential's id.</returns>
    public Task<Guid> SaveAsync(
        string supplierCode,
        SupplierEnvironment environment,
        Guid? agencyId,
        string merchantCode,
        string merchantKey,
        string? bearerToken,
        CancellationToken cancellationToken = default);
}
