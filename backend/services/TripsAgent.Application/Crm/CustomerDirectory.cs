using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// Finds the customer an inquiry, a quote or a booking belongs to — or starts one. Customers are
/// never keyed in first (FRD §2.8 RS-1); this is the only place one is created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Email first, then phone.</b> An email address is the stronger identity: a family or an office
/// can share a phone, but rarely an inbox. So a request with an email is matched by email only, and
/// otherwise becomes a new customer — except where the phone matches a customer who has no email yet,
/// who is taken to be the same person and has the email filled in. A request with only a phone is
/// matched by phone.
/// </para>
/// <para>
/// Everything is staged on the caller's context, which saves it with its own work. Two requests
/// racing to create the same email meet the unique index; the caller retries, and the retry finds
/// the customer the other request made (see <see cref="CrmSaves"/>).
/// </para>
/// </remarks>
public sealed class CustomerDirectory
{
    private readonly IAppDbContext _db;

    public CustomerDirectory(IAppDbContext db) => _db = db;

    /// <summary>The customer these details belong to, or a new one. Tracked: the caller saves.</summary>
    public async Task<Customer> FindOrAddAsync(
        Guid agencyId,
        string name,
        string? email,
        string? phone,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var tidyEmail = ContactDetails.NormaliseEmail(email);
        var phoneKey = ContactDetails.PhoneKey(phone);

        var customer = tidyEmail is not null
            ? await ByEmailAsync(tidyEmail, cancellationToken) ?? await ByPhoneAsync(phoneKey, withoutEmailOnly: true, cancellationToken)
            : await ByPhoneAsync(phoneKey, withoutEmailOnly: false, cancellationToken);

        if (customer is null)
        {
            customer = Customer.Create(agencyId, name, email, phone, now);
            _db.Customers.Add(customer);
            return customer;
        }

        await AddMissingContactAsync(customer, email, phone, cancellationToken);
        customer.RecordActivity(now);

        return customer;
    }

    /// <summary>
    /// Fills in the details <paramref name="customer"/> lacks — unless the email is already another
    /// customer's, which would make two records claim one inbox.
    /// </summary>
    public async Task AddMissingContactAsync(
        Customer customer,
        string? email,
        string? phone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var tidyEmail = ContactDetails.NormaliseEmail(email);

        if (customer.Email is null && tidyEmail is not null
            && (await ByEmailAsync(tidyEmail, cancellationToken) is { } holder && holder.Id != customer.Id))
        {
            tidyEmail = null;
        }

        customer.AddMissingContact(tidyEmail, phone);
    }

    private async Task<Customer?> ByEmailAsync(string email, CancellationToken cancellationToken) =>

        // The context first: the same unit of work may have just created them.
        _db.Customers.Local.FirstOrDefault(candidate => candidate.Email == email)
        ?? await _db.Customers.FirstOrDefaultAsync(candidate => candidate.Email == email, cancellationToken);

    private async Task<Customer?> ByPhoneAsync(string? phoneKey, bool withoutEmailOnly, CancellationToken cancellationToken)
    {
        if (phoneKey is null)
        {
            return null;
        }

        var local = _db.Customers.Local.FirstOrDefault(candidate =>
            candidate.PhoneKey == phoneKey && (!withoutEmailOnly || candidate.Email is null));

        if (local is not null)
        {
            return local;
        }

        var query = _db.Customers.Where(candidate => candidate.PhoneKey == phoneKey);

        if (withoutEmailOnly)
        {
            query = query.Where(candidate => candidate.Email == null);
        }

        // Several people can share a phone. The first one it was given for keeps it.
        return await query
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
