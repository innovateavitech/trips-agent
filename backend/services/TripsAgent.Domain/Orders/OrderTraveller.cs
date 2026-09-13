using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Orders;

/// <summary>
/// Who is travelling on a line, and the document they travel on.
/// </summary>
/// <remarks>
/// The passport number and its expiry are stored ONLY as ciphertext (issue 104). This type holds them
/// in clear, in memory; the persistence layer encrypts them on the way to the database and decrypts
/// them on the way back, with a value converter nobody has to remember to call. The audit log records
/// neither, and neither column can be searched.
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
        string? passportNumber = null,
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
            PassportNumber = string.IsNullOrWhiteSpace(passportNumber) ? null : passportNumber.Trim(),
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

    /// <summary>The passport number. Encrypted at rest; never logged, audited or searchable.</summary>
    public string? PassportNumber { get; private set; }

    /// <summary>The passport's expiry. Encrypted at rest, like the number.</summary>
    public DateOnly? PassportExpiry { get; private set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string? Nationality { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
