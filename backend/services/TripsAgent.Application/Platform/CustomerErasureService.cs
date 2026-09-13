using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;

namespace TripsAgent.Application.Platform;

/// <summary>What an erasure would touch, and what stands in its way.</summary>
/// <param name="CustomerId">The customer the request would be recorded against.</param>
/// <param name="Name">Their name as it stands, so whoever runs it can see they picked the right person.</param>
/// <param name="Email">Likewise their email, or null if they never gave one.</param>
/// <param name="Orders">Orders that stay, with their money, and stop naming anyone.</param>
/// <param name="Travellers">Traveller rows whose names and documents go.</param>
/// <param name="TravelDocuments">Passenger documents that are deleted outright.</param>
/// <param name="Notifications">Emails and messages whose recipient is replaced.</param>
/// <param name="EvidenceFiles">Uploaded dispute evidence that is deleted from blob storage.</param>
/// <param name="Blockers">Reasons this cannot be done yet. Empty means it can.</param>
public sealed record ErasurePreview(
    Guid CustomerId,
    string Name,
    string? Email,
    int Orders,
    int Travellers,
    int TravelDocuments,
    int Notifications,
    int EvidenceFiles,
    IReadOnlyList<string> Blockers);

/// <summary>What an erasure did: counts, never values.</summary>
/// <param name="RequestId">The row that records it, which survives the erasure.</param>
/// <param name="Completed">False when it was refused; <paramref name="Blockers"/> then says why.</param>
/// <param name="Changed">Rows changed, by table.</param>
/// <param name="Blockers">Why it was refused, when it was.</param>
public sealed record ErasureOutcome(
    Guid RequestId,
    bool Completed,
    IReadOnlyDictionary<string, int> Changed,
    IReadOnlyList<string> Blockers);

/// <summary>
/// NDPA erasure, carried out as anonymisation (issue 106).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does.</b> It replaces one person's details in place, everywhere this system holds
/// them, and leaves every financial and audit record standing: the ledger still balances, the order
/// lines still add up, the invoices still render, and the audit trail still says who did what. The
/// row keeps its shape; it stops identifying anyone. Why that reading of the NDPA, and what was
/// weighed against it, is in <c>docs/adr/0009-ndpa-erasure-as-anonymisation.md</c>.
/// </para>
/// <para>
/// <b>It cannot be undone.</b> Nothing is copied anywhere first — no archive table, no export, no
/// "erased_customers" backup. The only record that survives is <see cref="ErasureRequest"/>, which
/// holds who asked, when, why, and how many rows changed in which tables, and no personal detail at
/// all. A restore from a database backup would bring the details back, which is a limitation of
/// backups rather than of this, and the ADR says so.
/// </para>
/// <para>
/// <b>It is refused rather than half-done</b> while an order is still in flight or a dispute is
/// still open: those need the person's details to finish, and the refusal — with its reason — is
/// recorded exactly like a completed one.
/// </para>
/// <para>
/// Every change is a set-based <c>UPDATE</c> or <c>DELETE</c> rather than a load-modify-save, so the
/// audit interceptor never sees the old values: writing "before: Adaeze Okafor" into a table kept
/// for seven years is the one mistake an erasure must not make.
/// </para>
/// </remarks>
public sealed partial class CustomerErasureService
{
    /// <summary>What a name becomes. Deliberately not a blank: a blank looks like a bug.</summary>
    public const string ErasedName = "Erased at request";

    /// <summary>What a traveller's given name becomes.</summary>
    public const string ErasedFirstName = "Erased";

    /// <summary>What a traveller's family name becomes.</summary>
    public const string ErasedLastName = "Traveller";

    /// <summary>What a note about somebody becomes.</summary>
    public const string ErasedText = "[erased at the person's request]";

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly ITransactionRunner _transactions;
    private readonly IBlobStorage _storage;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerErasureService> _logger;

    public CustomerErasureService(
        IAppDbContext db,
        IPlatformScope platformScope,
        ITransactionRunner transactions,
        IBlobStorage storage,
        ITenantContext tenant,
        TimeProvider clock,
        ILogger<CustomerErasureService> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _transactions = transactions;
        _storage = storage;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Finds the person by the email they gave the agency, and says what erasing them would do.</summary>
    /// <remarks>
    /// By email because that is what a person writes in when they ask, and because <c>crm.customers</c>
    /// is the only place in the CRM their contact details live. Never by passport number or bank account:
    /// those columns are encrypted and cannot be searched at all (issue 104).
    /// </remarks>
    public async Task<ErasurePreview?> PreviewAsync(Guid agencyId, string email, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter($"NDPA erasure: finding the customer of agency {agencyId} to erase (issue 106)");

        var normalised = (email ?? string.Empty).Trim();

        var customer = await _db.Customers
            .Where(c => c.AgencyId == agencyId && c.Email != null && c.Email == normalised)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (customer is null)
        {
            return null;
        }

        var orderIds = await OrderIdsAsync(agencyId, customer.Id, cancellationToken);
        var lineIds = await LineIdsAsync(orderIds, cancellationToken);

        return new ErasurePreview(
            customer.Id,
            customer.Name,
            customer.Email,
            orderIds.Count,
            await _db.OrderTravellers.CountAsync(t => lineIds.Contains(t.OrderLineId), cancellationToken),
            await TravelDocumentsQuery(lineIds).CountAsync(cancellationToken),
            customer.Email is null ? 0 : await _db.Notifications.CountAsync(n => n.RecipientAddress == customer.Email, cancellationToken),
            (await EvidenceAssetIdsAsync(orderIds, cancellationToken)).Count,
            await BlockersAsync(orderIds, cancellationToken));
    }

    /// <summary>
    /// Erases the customer's details, in one transaction, and records the request either way.
    /// </summary>
    /// <param name="agencyId">The agency whose customer it is.</param>
    /// <param name="customerId">Who to erase, from <see cref="PreviewAsync"/>.</param>
    /// <param name="reason">Why. Stored, and required.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<ErasureOutcome> EraseAsync(
        Guid agencyId,
        Guid customerId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            $"NDPA erasure: anonymising customer {customerId} of agency {agencyId} on request (issue 106)");

        var now = _clock.GetUtcNow();
        var request = ErasureRequest.Record(agencyId, customerId, reason, _tenant.UserId, now);

        var customer = await _db.Customers.FirstOrDefaultAsync(
            c => c.Id == customerId && c.AgencyId == agencyId, cancellationToken);

        if (customer is null)
        {
            throw new InvalidOperationException($"There is no customer {customerId} at agency {agencyId}.");
        }

        var email = customer.Email;
        var orderIds = await OrderIdsAsync(agencyId, customerId, cancellationToken);
        var blockers = await BlockersAsync(orderIds, cancellationToken);

        if (blockers.Count > 0)
        {
            request.Refuse(string.Join(" ", blockers), now);
            _db.ErasureRequests.Add(request);
            await _db.SaveChangesAsync(cancellationToken);

            LogRefused(_logger, request.Id, blockers.Count);

            return new ErasureOutcome(request.Id, Completed: false, new Dictionary<string, int>(StringComparer.Ordinal), blockers);
        }

        // Outside the transaction: the bytes are gone from storage before the row that points at them
        // is cleared. The other way round would leave a file nothing remembers, which is worse.
        var evidenceFiles = await DeleteEvidenceFilesAsync(orderIds, cancellationToken);

        var changed = await _transactions.RunAsync(
            async token =>
            {
                var lineIds = await LineIdsAsync(orderIds, token);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);

                // The only copy of the person's contact details in the CRM.
                counts["crm.customers"] = await _db.Customers
                    .Where(c => c.Id == customerId)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(c => c.Name, ErasedName)
                            .SetProperty(c => c.Email, (string?)null)
                            .SetProperty(c => c.Phone, (string?)null)
                            .SetProperty(c => c.PhoneKey, (string?)null)
                            .SetProperty(c => c.UpdatedAt, now),
                        token);

                // Who travelled stays on the sale as a row; it stops being a person.
                counts["orders.order_travellers"] = await _db.OrderTravellers
                    .Where(t => lineIds.Contains(t.OrderLineId))
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(t => t.FirstName, ErasedFirstName)
                            .SetProperty(t => t.LastName, ErasedLastName)
                            .SetProperty(t => t.BirthDate, (DateOnly?)null)
                            .SetProperty(t => t.PassportNumber, (string?)null)
                            .SetProperty(t => t.PassportExpiry, (DateOnly?)null)
                            .SetProperty(t => t.Nationality, (string?)null)
                            .SetProperty(t => t.UpdatedAt, now),
                        token);

                // The supplier's copy of the same people. The ticket number stays: it is what ties the
                // airline's invoice to ours, and it names nobody.
                var passengerIds = await PassengerIdsAsync(lineIds, token);

                counts["supplier.passenger_documents"] = await _db.PassengerDocuments
                    .Where(document => passengerIds.Contains(document.PassengerId))
                    .ExecuteDeleteAsync(token);

                counts["supplier.supplier_booking_passengers"] = await _db.SupplierBookingPassengers
                    .Where(passenger => passengerIds.Contains(passenger.Id))
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(p => p.FirstName, ErasedFirstName)
                            .SetProperty(p => p.LastName, ErasedLastName)
                            .SetProperty(p => p.MiddleName, (string?)null)
                            .SetProperty(p => p.Title, (string?)null)
                            .SetProperty(p => p.BirthDate, (DateOnly?)null)
                            .SetProperty(p => p.Gender, (string?)null)
                            .SetProperty(p => p.Email, (string?)null)
                            .SetProperty(p => p.PhoneNumber, (string?)null),
                        token);

                // Who a departure's instalments were billed to. The money stays; the contact goes.
                counts["catalog.booking_payment_schedules"] = await _db.BookingPaymentSchedules
                    .Where(schedule => lineIds.Contains(schedule.OrderLineId))
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(s => s.ContactName, ErasedName)
                            .SetProperty(s => s.ContactEmail, (string?)null)
                            .SetProperty(s => s.UpdatedAt, now),
                        token);

                if (email is { Length: > 0 })
                {
                    // A waitlist entry is somebody asking to be told about a seat. The row is kept, because
                    // the seat maths counts it, and its address becomes one that can never be delivered to.
                    var unreachable = Unreachable(request.Id);

                    counts["catalog.departure_waitlist"] = await _db.DepartureWaitlist
                        .Where(entry => entry.AgencyId == agencyId && entry.Email == email)
                        .ExecuteUpdateAsync(
                            set => set
                                .SetProperty(e => e.Name, ErasedName)
                                .SetProperty(e => e.Email, unreachable),
                            token);

                    // What was sent to them, and what it said. The row proves a message was sent.
                    counts["notifications.notifications"] = await _db.Notifications
                        .Where(notification => notification.RecipientAddress == email)
                        .ExecuteUpdateAsync(
                            set => set
                                .SetProperty(n => n.RecipientAddress, unreachable)
                                .SetProperty(n => n.RecipientName, ErasedName)
                                .SetProperty(n => n.Payload, "{}")
                                .SetProperty(n => n.UpdatedAt, now),
                            token);
                }

                // A chargeback's evidence describes the customer in the agency's own words. The dispute,
                // its amount and its outcome are a financial record and stay exactly as they are.
                counts["payments.disputes"] = await _db.Disputes
                    .Where(dispute => dispute.OrderId != null && orderIds.Contains(dispute.OrderId.Value))
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(d => d.EvidenceNote, ErasedText)
                            .SetProperty(d => d.EvidencePayload, (string?)null)
                            .SetProperty(d => d.EvidenceAssetIds, (string?)null)
                            .SetProperty(d => d.UpdatedAt, now),
                        token);

                counts["platform.assets"] = evidenceFiles;

                request.Complete(JsonSerializer.Serialize(counts), now);
                _db.ErasureRequests.Add(request);
                await _db.SaveChangesAsync(token);

                return counts;
            },
            cancellationToken);

        LogErased(_logger, request.Id, changed.Values.Sum());

        return new ErasureOutcome(request.Id, Completed: true, changed, []);
    }

    /// <summary>The erasures carried out, newest first. Holds no personal detail, by design.</summary>
    public async Task<IReadOnlyList<ErasureRequest>> RecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("NDPA erasure: listing erasure requests across all agencies (issue 106)");

        return await _db.ErasureRequests
            .AsNoTracking()
            .OrderByDescending(request => request.RequestedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ the parts

    /// <summary>An address that exists nowhere and can never be delivered to. RFC 2606's reserved domain.</summary>
    private static string Unreachable(Guid requestId) => $"erased-{requestId:N}@invalid";

    private Task<List<Guid>> OrderIdsAsync(Guid agencyId, Guid customerId, CancellationToken cancellationToken) =>
        _db.Orders
            .Where(order => order.AgencyId == agencyId && order.CustomerId == customerId)
            .Select(order => order.Id)
            .ToListAsync(cancellationToken);

    private Task<List<Guid>> LineIdsAsync(List<Guid> orderIds, CancellationToken cancellationToken) =>
        _db.OrderLines
            .Where(line => orderIds.Contains(line.OrderId))
            .Select(line => line.Id)
            .ToListAsync(cancellationToken);

    private Task<List<Guid>> PassengerIdsAsync(List<Guid> lineIds, CancellationToken cancellationToken) =>
        _db.SupplierBookingPassengers
            .Where(passenger => _db.SupplierBookings
                .Any(booking => booking.Id == passenger.SupplierBookingId && lineIds.Contains(booking.OrderLineId)))
            .Select(passenger => passenger.Id)
            .ToListAsync(cancellationToken);

    private IQueryable<Domain.Suppliers.PassengerDocument> TravelDocumentsQuery(List<Guid> lineIds) =>
        _db.PassengerDocuments
            .Where(document => _db.SupplierBookingPassengers
                .Any(passenger => passenger.Id == document.PassengerId
                                  && _db.SupplierBookings.Any(booking =>
                                      booking.Id == passenger.SupplierBookingId && lineIds.Contains(booking.OrderLineId))));

    /// <summary>
    /// What has to finish before a person can be erased.
    /// </summary>
    /// <remarks>
    /// An order still being paid for or fulfilled needs the traveller's name to finish, and an open
    /// dispute is evidence a bank is still weighing. Both resolve in days; the person is told so, and
    /// the refusal is recorded with its reason.
    /// </remarks>
    private async Task<IReadOnlyList<string>> BlockersAsync(List<Guid> orderIds, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        var inFlight = await _db.Orders
            .Where(order => orderIds.Contains(order.Id)
                            && (order.Status == OrderStatus.PendingPayment || order.Status == OrderStatus.PartiallyFulfilled))
            .CountAsync(cancellationToken);

        if (inFlight > 0)
        {
            blockers.Add(
                $"{inFlight} order(s) are still being paid for or fulfilled. Erasing now would take away the "
                + "details the booking needs to finish. Complete or cancel them first.");
        }

        var openDisputes = await _db.Disputes
            .Where(dispute => dispute.OrderId != null
                              && orderIds.Contains(dispute.OrderId.Value)
                              && (dispute.Status == DisputeStatus.Open || dispute.Status == DisputeStatus.EvidenceSubmitted))
            .CountAsync(cancellationToken);

        if (openDisputes > 0)
        {
            blockers.Add(
                $"{openDisputes} chargeback(s) are still open. The evidence filed with the bank names the "
                + "customer, and withdrawing it now would lose the dispute by default.");
        }

        return blockers;
    }

    /// <summary>The asset ids named as evidence on this customer's disputes.</summary>
    private async Task<IReadOnlyList<Guid>> EvidenceAssetIdsAsync(List<Guid> orderIds, CancellationToken cancellationToken)
    {
        var payloads = await _db.Disputes
            .Where(dispute => dispute.OrderId != null
                              && orderIds.Contains(dispute.OrderId.Value)
                              && dispute.EvidenceAssetIds != null)
            .Select(dispute => dispute.EvidenceAssetIds!)
            .ToListAsync(cancellationToken);

        var ids = new List<Guid>();

        foreach (var payload in payloads)
        {
            try
            {
                ids.AddRange(JsonSerializer.Deserialize<List<Guid>>(payload) ?? []);
            }
            catch (JsonException)
            {
                // Evidence recorded in some other shape by a future change: the row's own columns are
                // still cleared below, and a file we cannot identify is reported rather than guessed at.
                LogUnreadableEvidence(_logger);
            }
        }

        return ids;
    }

    /// <summary>
    /// Deletes the uploaded evidence from blob storage, through the port, and marks each asset erased.
    /// </summary>
    /// <remarks>
    /// The row stays so the trail still shows a file was there and is not. The bytes do not.
    /// </remarks>
    private async Task<int> DeleteEvidenceFilesAsync(List<Guid> orderIds, CancellationToken cancellationToken)
    {
        var assetIds = await EvidenceAssetIdsAsync(orderIds, cancellationToken);

        if (assetIds.Count == 0)
        {
            return 0;
        }

        var assets = await _db.Assets
            .Where(asset => assetIds.Contains(asset.Id))
            .ToListAsync(cancellationToken);

        var now = _clock.GetUtcNow();

        foreach (var asset in assets)
        {
            await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
            asset.EraseContent(now);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return assets.Count;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Erasure request {RequestId} completed: {Rows} row(s) anonymised or deleted.")]
    private static partial void LogErased(ILogger logger, Guid requestId, int rows);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Erasure request {RequestId} refused: {Blockers} thing(s) must finish first. Nothing was changed.")]
    private static partial void LogRefused(ILogger logger, Guid requestId, int blockers);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A dispute's evidence asset list could not be read, so its files were left in storage. "
                  + "The dispute's own evidence columns were still cleared.")]
    private static partial void LogUnreadableEvidence(ILogger logger);
}
