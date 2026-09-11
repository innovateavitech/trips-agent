using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Security;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Orders;

/// <summary>One traveller on a line. The passport number arrives in clear and is never stored that way.</summary>
public sealed record PlaceOrderTraveller(
    TravellerType TravellerType,
    string FirstName,
    string LastName,
    DateOnly? BirthDate = null,
    string? PassportNumber = null,
    DateOnly? PassportExpiry = null,
    string? Nationality = null);

/// <summary>One thing being bought, named by the quote that priced it.</summary>
public sealed record PlaceOrderLine(
    Guid PriceQuoteId,
    string TitleSnapshot,
    string PaxBreakdown,
    Guid? ProductId = null,
    Guid? SupplierOfferId = null,
    IReadOnlyList<PlaceOrderTraveller>? Travellers = null);

/// <summary>An order to place for the current agency.</summary>
public sealed record PlaceOrderCommand(
    BuyerType BuyerType,
    OrderChannel Channel,
    Guid? CustomerId,
    IReadOnlyList<PlaceOrderLine> Lines);

/// <summary>
/// Places an order: prices frozen from their quotes, a gapless number, and the travellers, in one
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// Everything happens inside one transaction on purpose. The order number comes from the same
/// gapless allocator invoices use (#47), so a failure anywhere after the number is taken rolls the
/// number back with the order — an auditor reads a missing number as a missing order, and a
/// half-written order with a number is worse than no order at all.
/// </para>
/// <para>
/// Quotes are loaded through the ordinary tenant filter, so a quote belonging to another agency
/// simply is not found. Each one is re-checked for expiry as the line is built: a price nobody
/// re-confirmed is not a price we can sell at (issue #29).
/// </para>
/// <para>
/// The checkout saga (#42) owns what happens next — taking payment and booking with the supplier.
/// This only records what was sold, at what price.
/// </para>
/// </remarks>
public sealed class PlaceOrderHandler
{
    /// <summary>The purpose string the passport number is encrypted under. Never reuse it elsewhere.</summary>
    public const string PassportPurpose = "orders.order_travellers.passport_number";

    private readonly IAppDbContext _db;
    private readonly IDocumentNumberAllocator _numbers;
    private readonly ITransactionRunner _transactions;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public PlaceOrderHandler(
        IAppDbContext db,
        IDocumentNumberAllocator numbers,
        ITransactionRunner transactions,
        ISecretProtector protector,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _db = db;
        _numbers = numbers;
        _transactions = transactions;
        _protector = protector;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>Places <paramref name="command"/> and returns the order, with its lines attached.</summary>
    public Task<Order> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(command));
        }

        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException(
                "Placing an order needs an agency, and none is resolved for this request. An order "
                + "belongs to the agency that sold it.");

        return _transactions.RunAsync(async token =>
        {
            var now = _clock.GetUtcNow();

            var quoteIds = command.Lines.Select(line => line.PriceQuoteId).Distinct().ToArray();
            var quotes = await _db.PriceQuotes
                .Where(quote => quoteIds.Contains(quote.Id))
                .ToDictionaryAsync(quote => quote.Id, token);

            var lines = new List<OrderLine>(command.Lines.Count);

            foreach (var requested in command.Lines)
            {
                if (!quotes.TryGetValue(requested.PriceQuoteId, out var quote))
                {
                    // Either it does not exist or it is another agency's, and the difference is not
                    // this caller's business to learn.
                    throw new InvalidOperationException($"Price quote {requested.PriceQuoteId} was not found for this agency.");
                }

                lines.Add(OrderLine.FromQuote(
                    quote,
                    requested.TitleSnapshot,
                    requested.PaxBreakdown,
                    now,
                    requested.ProductId,
                    requested.SupplierOfferId));
            }

            var number = await _numbers.NextAsync(DocumentType.Order, now, token);

            var order = Order.Place(
                agencyId,
                number.Value,
                lines[0].Currency,
                command.BuyerType,
                command.Channel,
                command.CustomerId,
                lines,
                now);

            _db.Orders.Add(order);

            foreach (var (requested, line) in command.Lines.Zip(lines))
            {
                foreach (var traveller in requested.Travellers ?? [])
                {
                    _db.OrderTravellers.Add(OrderTraveller.Record(
                        agencyId,
                        line.Id,
                        traveller.TravellerType,
                        traveller.FirstName,
                        traveller.LastName,
                        traveller.BirthDate,
                        string.IsNullOrWhiteSpace(traveller.PassportNumber)
                            ? null
                            : _protector.Protect(traveller.PassportNumber, PassportPurpose),
                        traveller.PassportExpiry,
                        traveller.Nationality));
                }
            }

            await _db.SaveChangesAsync(token);

            return order;
        }, cancellationToken);
    }
}
