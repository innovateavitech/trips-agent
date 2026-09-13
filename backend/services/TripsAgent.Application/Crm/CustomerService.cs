using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Crm;

/// <summary>One customer's bookings, and what they came to.</summary>
/// <param name="Bookings">Their paid orders, newest first.</param>
/// <param name="LifetimeValueMinor">What those came to, in minor units.</param>
/// <param name="TotalBookings">How many there are.</param>
internal sealed record CustomerOrders(
    IReadOnlyList<CustomerBookingResponse> Bookings,
    long LifetimeValueMinor,
    int TotalBookings);

/// <summary>
/// The customer 360 (#62): who the agency's customers are, and everything each has asked, been
/// quoted and booked.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Customers are never keyed in — <see cref="CustomerDirectory"/> creates them from an
/// inquiry, a quote or a booking (FRD §2.8 RS-1) — so there is no create or edit here.
/// </para>
/// <para>
/// Lifetime value counts paid bookings only, and drops cancelled and refunded ones: it is what the
/// customer is worth to the agency, not what they once agreed to.
/// </para>
/// </remarks>
public sealed class CustomerService
{
    private readonly IAppDbContext _db;
    private readonly CrmContext _crm;
    private readonly CrmReader _reader;

    public CustomerService(IAppDbContext db, CrmContext crm, CrmReader reader)
    {
        _db = db;
        _crm = crm;
        _reader = reader;
    }

    /// <summary>Every customer the agency has, most recently active first.</summary>
    public async Task<List<CustomerSummaryResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var customers = await _db.Customers.AsNoTracking()
            .OrderByDescending(customer => customer.LastActivityAt)
            .ThenBy(customer => customer.Name)
            .ToListAsync(cancellationToken);

        if (customers.Count == 0)
        {
            return [];
        }

        var ids = customers.Select(customer => customer.Id).ToList();
        var openLeads = await OpenLeadCountsAsync(ids, cancellationToken);
        var orders = await OrdersAsync(ids, cancellationToken);

        return customers
            .Select(customer =>
            {
                var totals = orders.GetValueOrDefault(customer.Id);

                return new CustomerSummaryResponse(
                    customer.Id,
                    customer.Name,
                    customer.Email,
                    customer.Phone,
                    totals?.LifetimeValueMinor ?? 0,
                    totals?.TotalBookings ?? 0,
                    customer.LastActivityAt,
                    openLeads.GetValueOrDefault(customer.Id));
            })
            .ToList();
    }

    /// <summary>One customer with everything around them, or null when the agency has no such customer.</summary>
    public async Task<CustomerResponse?> GetAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var agency = await _crm.AgencyAsync(cancellationToken);

        var customer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken);

        if (customer is null)
        {
            return null;
        }

        var leadIds = await _db.Leads.AsNoTracking()
            .Where(lead => lead.CustomerId == customerId)
            .Select(lead => lead.Id)
            .ToListAsync(cancellationToken);

        var leads = await _reader.LeadSummariesAsync(_db.Leads.Where(lead => lead.CustomerId == customerId), cancellationToken);

        var quotes = await CrmReader.QuoteSummariesAsync(
            _db.Quotes.Where(quote => leadIds.Contains(quote.LeadId)), agency.Today, cancellationToken);

        var tasks = await _reader.TasksAsync(_db.FollowUpTasks.Where(task => task.CustomerId == customerId), cancellationToken);

        var messages = await CrmReader.CommunicationsAsync(
            _db.Communications.Where(message => message.CustomerId == customerId), cancellationToken);

        var orders = (await OrdersAsync([customerId], cancellationToken)).GetValueOrDefault(customerId);
        var openLeads = (await OpenLeadCountsAsync([customerId], cancellationToken)).GetValueOrDefault(customerId);

        return new CustomerResponse(
            customer.Id,
            customer.Name,
            customer.Email,
            customer.Phone,
            orders?.LifetimeValueMinor ?? 0,
            orders?.TotalBookings ?? 0,
            customer.LastActivityAt,
            openLeads,
            agency.Currency,
            customer.CreatedAt,
            leads,
            quotes,
            orders?.Bookings ?? [],
            tasks,
            messages);
    }

    /// <summary>How many leads each of these customers has that are neither Won nor Lost.</summary>
    private async Task<Dictionary<Guid, int>> OpenLeadCountsAsync(
        IReadOnlyList<Guid> customerIds,
        CancellationToken cancellationToken) =>
        await _db.Leads.AsNoTracking()
            .Where(lead => customerIds.Contains(lead.CustomerId)
                && lead.Stage != LeadStage.Won
                && lead.Stage != LeadStage.Lost)
            .GroupBy(lead => lead.CustomerId)
            .Select(group => new { CustomerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.CustomerId, entry => entry.Count, cancellationToken);

    /// <summary>
    /// Each customer's bookings, and what they came to. One query for every customer asked about:
    /// the list screen reads a page of customers without a query per row.
    /// </summary>
    private async Task<Dictionary<Guid, CustomerOrders>> OrdersAsync(
        IReadOnlyList<Guid> customerIds,
        CancellationToken cancellationToken)
    {
        var orders = await _db.Orders.AsNoTracking()
            .Where(order => order.CustomerId != null && customerIds.Contains(order.CustomerId.Value))
            .OrderByDescending(order => order.PlacedAt ?? order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Select(order => new
            {
                order.Id,
                CustomerId = order.CustomerId!.Value,
                order.OrderNumber,
                order.Status,
                Total = order.TotalGrossMinor,
            })
            .ToListAsync(cancellationToken);

        if (orders.Count == 0)
        {
            return [];
        }

        // What the booking is called on the customer's record: its first line's title, snapshotted
        // when the order was placed. Ids are version 7 GUIDs, so ordering by id is the order added.
        var orderIds = orders.Select(order => order.Id).ToList();

        var titles = await _db.OrderLines.AsNoTracking()
            .Where(line => orderIds.Contains(line.OrderId))
            .OrderBy(line => line.Id)
            .Select(line => new { line.OrderId, line.TitleSnapshot })
            .ToListAsync(cancellationToken);

        var firstTitle = titles
            .GroupBy(line => line.OrderId)
            .ToDictionary(group => group.Key, group => group.First().TitleSnapshot);

        return orders
            .GroupBy(order => order.CustomerId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var rows = group
                        .Select(order => new CustomerBookingResponse(
                            order.OrderNumber,
                            firstTitle.GetValueOrDefault(order.Id, order.OrderNumber),

                            // Order lines carry no departure date yet — the supplier's booking holds it,
                            // and the CRM does not read supplier records. Null until they do.
                            null,
                            CustomerBookings.Describe(order.Status),
                            order.Total.AmountMinor))
                        .ToList();

                    var counted = group.Where(order => CustomerBookings.CountsTowardValue(order.Status)).ToList();

                    return new CustomerOrders(rows, counted.Sum(order => order.Total.AmountMinor), counted.Count);
                });
    }
}
