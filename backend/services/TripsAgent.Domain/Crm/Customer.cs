using TripsAgent.Domain.Common;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Domain.Crm;

/// <summary>
/// One of the agency's own customers: a traveller who asked, was quoted, or booked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never keyed in first</b> (FRD §2.8 RS-1). A customer is created by the first inquiry, quote or
/// booking that names them, and found again by email or phone after that — see
/// <see cref="ContactDetails"/>. There is no "new customer" form anywhere, on purpose.
/// </para>
/// <para>
/// <b>Personal data the agency controls.</b> The name, email and phone live on this row and nowhere
/// else in the CRM: leads, quotes, tasks and messages point at the customer rather than copying the
/// details, so erasing a person (#106) is one row anonymised in place. For the same reason this
/// entity is kept out of the platform audit log, which holds its before-and-after copies for seven
/// years.
/// </para>
/// <para>
/// What a customer has spent, and how many bookings they have made, is worked out from their orders
/// when it is read rather than stored here: an order is paid, refunded or cancelled by code that
/// knows nothing about the CRM, and a stored total would quietly drift from the truth.
/// </para>
/// </remarks>
public sealed class Customer : Entity, IAuditableEntity, ITenantScoped
{
    private Customer()
    {
        Name = string.Empty;
    }

    /// <summary>A customer first met through an inquiry, a quote or a booking.</summary>
    public static Customer Create(Guid agencyId, string name, string? email, string? phone, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var customer = new Customer
        {
            AgencyId = agencyId,
            Name = name.Trim(),
            LastActivityAt = now.ToUniversalTime(),
        };

        customer.AddMissingContact(email, phone);

        return customer;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Lower-cased. Unique within the agency when present.</summary>
    public string? Email { get; private set; }

    /// <summary>As it was written, tidied: <c>0803 000 1122</c>.</summary>
    public string? Phone { get; private set; }

    /// <summary>The digits that identify <see cref="Phone"/>, for matching. See <see cref="ContactDetails.PhoneKey"/>.</summary>
    public string? PhoneKey { get; private set; }

    /// <summary>The last time this customer asked, was quoted, wrote, or booked.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Fills in the contact details this record does not have yet. Never replaces one it has.</summary>
    /// <remarks>
    /// The storefront's trip-request form is anonymous: anyone can type anyone's email into it. Filling
    /// a blank is the most such a form may do, so a stranger can never change the email or phone an
    /// agency already has for its customer. Correcting a detail is a job for the agency itself.
    /// </remarks>
    public void AddMissingContact(string? email, string? phone)
    {
        if (Email is null && ContactDetails.NormaliseEmail(email) is { } tidyEmail)
        {
            Email = tidyEmail;
        }

        if (PhoneKey is null && ContactDetails.PhoneKey(phone) is { } key)
        {
            Phone = ContactDetails.TidyPhone(phone);
            PhoneKey = key;
        }
    }

    /// <summary>Notes that the customer did something. Only ever moves forward.</summary>
    public void RecordActivity(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();

        if (utc > LastActivityAt)
        {
            LastActivityAt = utc;
        }
    }
}

/// <summary>How a customer's bookings are read for their record in the CRM.</summary>
public static class CustomerBookings
{
    /// <summary>
    /// True when a booking counts toward a customer's bookings and lifetime value: it has been paid,
    /// and the money has stayed with the agency.
    /// </summary>
    /// <remarks>
    /// An order waiting for payment is not a booking yet, and a cancelled or refunded one is money the
    /// customer got back. A partly failed order counts: it was paid, and the resolution queue rebooks
    /// or refunds its failed lines one by one.
    /// </remarks>
    public static bool CountsTowardValue(OrderStatus status) =>
        status is OrderStatus.Paid or OrderStatus.PartiallyFulfilled or OrderStatus.Confirmed or OrderStatus.PartiallyFailed;

    /// <summary>An order's status as the agent reads it on the customer's record.</summary>
    public static string Describe(OrderStatus status) => status switch
    {
        OrderStatus.PendingPayment => "Awaiting payment",
        OrderStatus.Paid => "Paid",
        OrderStatus.PartiallyFulfilled => "Partly confirmed",
        OrderStatus.Confirmed => "Confirmed",
        OrderStatus.PartiallyFailed => "Needs attention",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Refunded => "Refunded",
        _ => status.ToString(),
    };
}
