using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// The merchant credentials we authenticate to a supplier with — held only as ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type never holds a plaintext secret.</b> The merchant key and bearer token arrive
/// already encrypted and leave still encrypted; decrypting them is the credential store's job, at
/// the moment an adapter needs them, and nowhere else. So there is no property on this entity that
/// could be logged, serialised into an API response or copied into the audit trail as a usable key.
/// </para>
/// <para>
/// <see cref="AgencyId"/> is nullable on purpose. Open question 1 in the delivery plan — does each
/// agency hold its own Trips Africa merchant account, or does the platform transact as one? — is
/// not answered. Null means a platform-level credential; a value means the agency's own. The
/// schema supports both answers, and which one is in force is a configuration setting, not a
/// migration.
/// </para>
/// <para>
/// Not <see cref="ITenantScoped"/>, because that requires a non-null agency. Its query filter is
/// written out in <c>AppDbContext</c> instead, the same way roles and users are.
/// </para>
/// </remarks>
public sealed class SupplierCredential : Entity, IAuditableEntity, IAuditLogged
{
    private SupplierCredential()
    {
        MerchantCode = string.Empty;
        MerchantKeyEncrypted = [];
    }

    /// <summary>Records a new credential. Both secrets must already be encrypted.</summary>
    public static SupplierCredential Create(
        Guid supplierId,
        Guid? agencyId,
        SupplierEnvironment environment,
        string merchantCode,
        byte[] merchantKeyEncrypted,
        byte[]? bearerTokenEncrypted,
        DateTimeOffset at)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);

        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty agency id is neither an agency nor the platform. Pass null for a platform-level credential.",
                nameof(agencyId));
        }

        var credential = new SupplierCredential
        {
            SupplierId = supplierId,
            AgencyId = agencyId,
            Environment = environment,
        };

        credential.Rotate(merchantCode, merchantKeyEncrypted, bearerTokenEncrypted, at);
        return credential;
    }

    public Guid SupplierId { get; private set; }

    /// <summary>Null for a platform-level credential; the owning agency otherwise.</summary>
    public Guid? AgencyId { get; private set; }

    public SupplierEnvironment Environment { get; private set; }

    /// <summary>The supplier's identifier for the merchant. Not a secret — it is sent in clear headers.</summary>
    public string MerchantCode { get; private set; }

    /// <summary>The merchant key, encrypted. Keys the price-confirmation hash, so it is the crown jewel.</summary>
    public byte[] MerchantKeyEncrypted { get; private set; }

    /// <summary>A static bearer token, encrypted, for suppliers (or products) that use one.</summary>
    public byte[]? BearerTokenEncrypted { get; private set; }

    /// <summary>When the secrets were last set.</summary>
    public DateTimeOffset RotatedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this is the platform's own merchant account rather than an agency's.</summary>
    public bool IsPlatformLevel => AgencyId is null;

    /// <summary>Replaces the secrets in place. The previous ciphertext is gone once saved.</summary>
    public void Rotate(string merchantCode, byte[] merchantKeyEncrypted, byte[]? bearerTokenEncrypted, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantCode);
        ArgumentNullException.ThrowIfNull(merchantKeyEncrypted);

        if (merchantKeyEncrypted.Length == 0)
        {
            throw new ArgumentException("The encrypted merchant key is empty.", nameof(merchantKeyEncrypted));
        }

        if (bearerTokenEncrypted is { Length: 0 })
        {
            throw new ArgumentException("Pass null rather than an empty bearer token.", nameof(bearerTokenEncrypted));
        }

        MerchantCode = merchantCode.Trim();
        MerchantKeyEncrypted = merchantKeyEncrypted;
        BearerTokenEncrypted = bearerTokenEncrypted;
        RotatedAt = at;
    }

    /// <summary>Identifies the credential without any part of either secret.</summary>
    public override string ToString() =>
        $"SupplierCredential {Id} ({Environment}, {(AgencyId is null ? "platform" : AgencyId.ToString())})";
}
