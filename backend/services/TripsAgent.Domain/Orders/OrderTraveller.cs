using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// Who is travelling on a line, and the document they travel on.
/// </summary>
/// <remarks>
/// The passport number is held ONLY as ciphertext: this type never sees the number in clear, because
/// encrypting is the application's job (ISecretProtector) and the domain depends on nothing. Nothing
/// in this class can therefore log or leak it, and the audit log redacts the column by name.
/// </remarks>
public sealed class OrderTraveller : Entity, IAuditableEntity, ITenantScoped
{
    private OrderTraveller()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    public static OrderTraveller Record(
        Guid agencyId,
        Guid orderLineId,
        TravellerType travellerType,
        string firstName,
        string lastName,
        DateOnly? birthDate = null,
        byte[]? passportNumberEncrypted = null,
        DateOnly? passportExpiry = null,
        string? nationality = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        return new OrderTraveller
        {
            AgencyId = agencyId,
            OrderLineId = orderLineId,
            TravellerType = travellerType,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            BirthDate = birthDate,
            PassportNumberEncrypted = passportNumberEncrypted,
            PassportExpiry = passportExpiry,
            Nationality = nationality?.Trim().ToUpperInvariant(),
        };
    }

    public Guid OrderLineId { get; private set; }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public TravellerType TravellerType { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    /// <summary>The passport number, encrypted at rest. Never stored or logged in clear.</summary>
    public byte[]? PassportNumberEncrypted { get; private set; }

    public DateOnly? PassportExpiry { get; private set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string? Nationality { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
