using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Documents;

/// <summary>What one run of the document worker came to.</summary>
public enum DocumentRunOutcome
{
    /// <summary>Every document asked for is rendered, and emailed where there was an address to email.</summary>
    Completed = 1,

    /// <summary>Nothing to do: the order is not there for this agency, or nothing on it is confirmed.</summary>
    NothingToDo = 2,

    /// <summary>A render failed and will be tried again.</summary>
    RetryLater = 3,

    /// <summary>A render failed for the last time, or could never work. Marked failed for a person.</summary>
    GaveUp = 4,
}

/// <summary>A document would have carried our brand to a traveller. Rendering it again cannot help.</summary>
public sealed class DocumentBrandException : Exception
{
    public DocumentBrandException(string message)
        : base(message)
    {
    }

    public DocumentBrandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DocumentBrandException()
        : base("The document would carry the platform's brand to a traveller.")
    {
    }
}

/// <summary>
/// Issues, renders, stores and emails an order's invoice and vouchers (#46). Runs in the Worker for
/// <c>documents.render</c> — never inside a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three steps, each saved before the next,</b> so a failure part-way is resumed rather than repeated:
/// </para>
/// <list type="number">
/// <item><b>Number.</b> Every missing document — the invoice, and a voucher per confirmed line — is
/// numbered in one short transaction, which is all the numbering lock is held for. The database's
/// unique index on first issues means a second run finds them rather than issuing more.</item>
/// <item><b>Render and store.</b> Each document not yet ready is drawn, stored under a key of its own,
/// recorded as an asset and marked ready. A ready document is never drawn again: its file is final.</item>
/// <item><b>Email.</b> Once every document is ready, one email carries them all to the customer, keyed
/// so a rerun queues it once.</item>
/// </list>
/// <para>
/// <b>Acting as the order's agency.</b> The Worker has no tenant of its own. Rather than reading
/// across every agency, it takes the agency from the message — written in that agency's own
/// transaction — and acts as that agency alone, so the tenant filter and row-level security confine
/// it exactly as they would a request. It is also the only way a document can be numbered: the
/// allocator takes the agency from the tenant, never from a parameter.
/// </para>
/// </remarks>
public sealed partial class OrderDocumentService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IDocumentNumberAllocator _numbers;
    private readonly ITransactionRunner _transactions;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly IDocumentRenderer _renderer;
    private readonly IBlobStorage _storage;
    private readonly IAgencyLogoSource _logos;
    private readonly ISupplierBookingReader _supplierBookings;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrderDocumentService> _logger;

    public OrderDocumentService(
        IAppDbContext db,
        ITenantContext tenant,
        IDocumentNumberAllocator numbers,
        ITransactionRunner transactions,
        IUniqueViolationDetector uniqueViolations,
        IDocumentRenderer renderer,
        IBlobStorage storage,
        IAgencyLogoSource logos,
        ISupplierBookingReader supplierBookings,
        INotifier notifier,
        TimeProvider clock,
        ILogger<OrderDocumentService> logger)
    {
        _db = db;
        _tenant = tenant;
        _numbers = numbers;
        _transactions = transactions;
        _uniqueViolations = uniqueViolations;
        _renderer = renderer;
        _storage = storage;
        _logos = logos;
        _supplierBookings = supplierBookings;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Issues whatever the order is missing, renders it, and emails the lot to the customer.</summary>
    /// <exception cref="InvalidOperationException">The caller is not acting as the order's agency.</exception>
    public async Task<DocumentRunOutcome> IssueForOrderAsync(OrderDocumentsRequested request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureActingAs(request.AgencyId);

        var order = await LoadOrderAsync(request.OrderId, cancellationToken);

        if (order is null)
        {
            LogOrderNotFound(_logger, request.OrderId, request.AgencyId);
            return DocumentRunOutcome.NothingToDo;
        }

        var confirmed = ConfirmedLines(order);

        if (confirmed.Count == 0)
        {
            LogNothingConfirmed(_logger, order.Id);
            return DocumentRunOutcome.NothingToDo;
        }

        var recipient = new DocumentRecipient(request.RecipientName, request.RecipientEmail);
        var documents = await EnsureNumberedAsync(order, confirmed, recipient, cancellationToken);

        var outcome = await RenderAsync(order, documents, cancellationToken);

        if (outcome != DocumentRunOutcome.Completed)
        {
            return outcome;
        }

        await EmailAsync(
            order,
            documents,
            NotificationTemplateCatalog.DocumentsIssued,
            $"{NotificationTemplateCatalog.DocumentsIssued}:{order.Id}",
            cancellationToken);

        return DocumentRunOutcome.Completed;
    }

    /// <summary>Renders one document that is already numbered — a reissue — and emails it.</summary>
    /// <exception cref="InvalidOperationException">The caller is not acting as the document's agency.</exception>
    public async Task<DocumentRunOutcome> RenderDocumentAsync(DocumentRenderRequested request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureActingAs(request.AgencyId);

        var document = await _db.GeneratedDocuments.FirstOrDefaultAsync(d => d.Id == request.DocumentId, cancellationToken);

        if (document?.OrderId is not { } orderId || await LoadOrderAsync(orderId, cancellationToken) is not { } order)
        {
            LogDocumentNotFound(_logger, request.DocumentId, request.AgencyId);
            return DocumentRunOutcome.NothingToDo;
        }

        var outcome = await RenderAsync(order, [document], cancellationToken);

        if (outcome != DocumentRunOutcome.Completed)
        {
            return outcome;
        }

        // A reissue says it replaces what the traveller already has; anything else is a first issue.
        var template = document.IssueNumber > 1
            ? NotificationTemplateCatalog.DocumentsReissued
            : NotificationTemplateCatalog.DocumentsIssued;

        await EmailAsync(order, [document], template, $"{template}:{document.Id}", cancellationToken);

        return DocumentRunOutcome.Completed;
    }

    // ------------------------------------------------------------------ step 1: number

    private async Task<List<GeneratedDocument>> EnsureNumberedAsync(
        Order order,
        IReadOnlyList<OrderLine> confirmed,
        DocumentRecipient recipient,
        CancellationToken cancellationToken)
    {
        try
        {
            return await NumberMissingAsync(order, confirmed, recipient, cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            // Another delivery of the same message numbered them first. Its documents stand, and
            // ours rolled back together with the numbers they took — so the sequence has no gap.
            _db.ChangeTracker.Clear();
            LogLostNumberingRace(_logger, order.Id);

            return await NumberMissingAsync(order, confirmed, recipient, cancellationToken);
        }
    }

    private Task<List<GeneratedDocument>> NumberMissingAsync(
        Order order,
        IReadOnlyList<OrderLine> confirmed,
        DocumentRecipient recipient,
        CancellationToken cancellationToken) =>
        _transactions.RunAsync(
            async token =>
            {
                // Read inside the transaction, so a replay starts from what is committed.
                var firstIssues = await _db.GeneratedDocuments
                    .Where(d => d.OrderId == order.Id && d.IssueNumber == 1)
                    .ToListAsync(token);

                var issuedAt = _clock.GetUtcNow();

                if (!firstIssues.Any(d => d.DocumentType == DocumentType.Invoice))
                {
                    firstIssues.Add(await IssueAsync(order, DocumentType.Invoice, null, recipient, issuedAt, token));
                }

                foreach (var line in confirmed)
                {
                    if (!firstIssues.Any(d => d.DocumentType == DocumentType.Voucher && d.OrderLineId == line.Id))
                    {
                        firstIssues.Add(await IssueAsync(order, DocumentType.Voucher, line.Id, recipient, issuedAt, token));
                    }
                }

                await _db.SaveChangesAsync(token);

                // The invoice, and the vouchers for what is confirmed now: a voucher for a line that
                // was confirmed and has since been refunded is not sent again.
                return firstIssues
                    .Where(d => d.DocumentType == DocumentType.Invoice || confirmed.Any(line => line.Id == d.OrderLineId))
                    .OrderBy(d => d.DocumentType)
                    .ThenBy(d => d.OrderLineId)
                    .ToList();
            },
            cancellationToken);

    private async Task<GeneratedDocument> IssueAsync(
        Order order,
        DocumentType type,
        Guid? orderLineId,
        DocumentRecipient recipient,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        var number = await _numbers.NextAsync(type, issuedAt, cancellationToken);
        var document = GeneratedDocument.IssueForOrder(order.AgencyId, number, issuedAt, order.Id, orderLineId, recipient);

        _db.GeneratedDocuments.Add(document);

        return document;
    }

    // ------------------------------------------------------------------ step 2: render and store

    private async Task<DocumentRunOutcome> RenderAsync(
        Order order,
        IReadOnlyList<GeneratedDocument> documents,
        CancellationToken cancellationToken)
    {
        var pending = documents.Where(d => !d.IsReady).ToList();

        if (pending.Count == 0)
        {
            return DocumentRunOutcome.Completed;
        }

        var sources = await LoadSourcesAsync(order, pending, cancellationToken);

        foreach (var document in pending)
        {
            // Counted, and saved, before anything is drawn: a Worker that dies mid-render has still
            // used an attempt, so a document that crashes the renderer cannot loop for ever.
            document.BeginRender();
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                var model = BuildModel(document, order, sources);
                EnsureNoPlatformBrand(model);

                await StoreAsync(document, _renderer.Render(model), cancellationToken);

                LogRendered(_logger, document.DocumentNumber, document.Id, order.AgencyId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await RecordFailureAsync(document.Id, ex);
            }
        }

        return DocumentRunOutcome.Completed;
    }

    private async Task StoreAsync(GeneratedDocument document, RenderedDocument rendered, CancellationToken cancellationToken)
    {
        // One key per document, derived from its id. Nothing writes it again once the document is
        // ready; before then a retry overwrites what a failed attempt left, which nobody was served.
        var key = AssetRules.GeneratedDocumentKey(document.AgencyId, document.Id);

        StoredBlob stored;
        using (var content = new MemoryStream(rendered.Pdf, writable: false))
        {
            stored = await _storage.StoreAsync(content, key, MediaTypes.Pdf, cancellationToken);
        }

        var now = _clock.GetUtcNow();
        var asset = Asset.RecordGenerated(
            document.AgencyId, document.FileName, key, MediaTypes.Pdf, stored.SizeBytes, stored.Checksum, now);

        _db.Assets.Add(asset);
        document.MarkRendered(asset.Id, stored.Checksum, stored.SizeBytes, rendered.TemplateKey, rendered.TemplateVersion, now);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<DocumentRunOutcome> RecordFailureAsync(Guid documentId, Exception failure)
    {
        // The failed attempt may have staged an asset that was never saved; the next save on this
        // context would send it again. Start clean, from what is committed.
        _db.ChangeTracker.Clear();

        var document = await _db.GeneratedDocuments.FirstAsync(d => d.Id == documentId, CancellationToken.None);

        if (document.IsReady)
        {
            // It did save, and something after the save failed. The rest are for the next delivery.
            return DocumentRunOutcome.RetryLater;
        }

        var gaveUp = document.RecordRenderFailure(
            $"{failure.GetType().Name}: {failure.Message}",
            permanent: failure is DocumentBrandException);

        await _db.SaveChangesAsync(CancellationToken.None);

        if (gaveUp)
        {
            LogGaveUp(_logger, failure, document.DocumentNumber, document.Id, document.RenderAttempts);
            return DocumentRunOutcome.GaveUp;
        }

        LogWillRetry(_logger, failure, document.DocumentNumber, document.Id, document.RenderAttempts);
        return DocumentRunOutcome.RetryLater;
    }

    /// <summary>Everything the models are built from, read once for the whole order.</summary>
    private async Task<RenderSources> LoadSourcesAsync(
        Order order,
        IReadOnlyList<GeneratedDocument> documents,
        CancellationToken cancellationToken)
    {
        var agency = await _db.Agencies.AsNoTracking().FirstAsync(a => a.Id == order.AgencyId, cancellationToken);
        var branding = await _db.AgencyBranding.AsNoTracking()
            .FirstOrDefaultAsync(b => b.AgencyId == order.AgencyId, cancellationToken);
        var logo = await _logos.LoadAsync(order.AgencyId, cancellationToken);

        var lineIds = order.Lines.Select(line => line.Id).ToList();
        var offerIds = order.Lines
            .Where(line => line.SupplierOfferId is not null)
            .Select(line => line.SupplierOfferId!.Value)
            .ToList();

        var travellers = await _db.OrderTravellers.AsNoTracking()
            .Where(traveller => lineIds.Contains(traveller.OrderLineId))
            .OrderBy(traveller => traveller.Id)
            .ToListAsync(cancellationToken);

        var bookings = (await _supplierBookings.ForOrderLinesAsync(lineIds, cancellationToken))
            .ToDictionary(booking => booking.OrderLineId);

        var flights = await _db.FlightSegments.AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .OrderBy(segment => segment.LegIndex)
            .ThenBy(segment => segment.SegmentIndex)
            .ToListAsync(cancellationToken);

        var buses = await _db.BusSegments.AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .OrderBy(segment => segment.DepartureAt)
            .ToListAsync(cancellationToken);

        var replacedIds = documents
            .Where(d => d.SupersedesDocumentId is not null)
            .Select(d => d.SupersedesDocumentId!.Value)
            .ToList();

        var replacedNumbers = await _db.GeneratedDocuments.AsNoTracking()
            .Where(d => replacedIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, cancellationToken);

        var brand = new DocumentBrand(
            agency.TradingName ?? agency.LegalName,
            branding?.PrimaryColor ?? AgencyBranding.DefaultPrimaryColor,
            logo?.Png,
            branding?.ContactAddress,
            agency.TaxId);

        return new RenderSources(
            brand,
            TimeZoneInfo.FindSystemTimeZoneById(agency.Timezone),
            travellers,
            bookings,
            flights,
            buses,
            replacedNumbers);
    }

    private static DocumentRenderModel BuildModel(GeneratedDocument document, Order order, RenderSources sources)
    {
        var model = new DocumentRenderModel
        {
            DocumentType = document.DocumentType,
            DocumentNumber = document.DocumentNumber,
            IssueNumber = document.IssueNumber,
            SupersedesDocumentNumber = document.SupersedesDocumentId is { } replaced
                                       && sources.ReplacedNumbers.TryGetValue(replaced, out var number)
                ? number
                : null,
            IssuedAt = document.IssuedAt,
            IssuedOn = TimeZoneInfo.ConvertTime(document.IssuedAt, sources.TimeZone)
                .ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            OrderNumber = order.OrderNumber,
            Currency = order.Currency,
            Brand = sources.Brand,
            RecipientName = document.RecipientName ?? string.Empty,
        };

        if (document.DocumentType == DocumentType.Invoice)
        {
            var lines = ConfirmedLines(order);

            return model with
            {
                Lines = lines
                    .Select(line => new DocumentLineItem(
                        line.TitleSnapshot,
                        line.ItemType,
                        Math.Max(1, sources.Travellers.Count(t => t.OrderLineId == line.Id)),
                        line.GrossAmountMinor.AmountMinor))
                    .ToList(),
                TotalMinor = lines.Sum(line => line.GrossAmountMinor.AmountMinor),
                PaymentStatus = PaymentStatusFor(order.Status),
            };
        }

        var voucherLine = order.Lines.Single(line => line.Id == document.OrderLineId);
        sources.Bookings.TryGetValue(voucherLine.Id, out var booking);

        return model with
        {
            ProductType = voucherLine.ItemType,
            ProductTitle = voucherLine.TitleSnapshot,
            SupplierReference = booking?.Pnr,
            Travellers = TravellersOn(voucherLine, booking, sources),
            Segments = SegmentsOf(voucherLine, sources),
        };
    }

    /// <summary>
    /// The supplier's passengers when there are some — they carry the ticket numbers and seats —
    /// otherwise the travellers recorded on the order.
    /// </summary>
    private static List<DocumentTraveller> TravellersOn(OrderLine line, SupplierBookingSnapshot? booking, RenderSources sources)
    {
        if (booking is { Passengers.Count: > 0 })
        {
            return booking.Passengers
                .Select(passenger => new DocumentTraveller(
                    FullName(passenger.FirstName, passenger.MiddleName, passenger.LastName),
                    passenger.PassengerType.ToString(),
                    passenger.TicketNumber,
                    Seats(passenger.SeatNumbersJson)))
                .ToList();
        }

        return sources.Travellers
            .Where(traveller => traveller.OrderLineId == line.Id)
            .Select(traveller => new DocumentTraveller(
                FullName(traveller.FirstName, null, traveller.LastName), traveller.TravellerType.ToString(), null, null))
            .ToList();
    }

    private static List<DocumentSegment> SegmentsOf(OrderLine line, RenderSources sources)
    {
        if (line.SupplierOfferId is not { } offerId)
        {
            return [];
        }

        return line.ItemType switch
        {
            OrderLineItemType.Flight => sources.Flights
                .Where(segment => segment.SupplierOfferId == offerId)
                .Select(segment => new DocumentSegment(
                    $"{segment.MarketingCarrier} {segment.FlightNumber}",
                    segment.OriginIata,
                    segment.DestinationIata,
                    WallClock(segment.DepartureAt),
                    WallClock(segment.ArrivalAt),
                    FlightDetail(segment)))
                .ToList(),

            // A bus terminal is a supplier's internal id, meaningless on paper; the title names the route.
            OrderLineItemType.Bus => sources.Buses
                .Where(segment => segment.SupplierOfferId == offerId)
                .Select(segment => new DocumentSegment(
                    segment.OperatorName,
                    null,
                    null,
                    WallClock(segment.DepartureAt),
                    segment.ArrivalAt is { } arrives ? WallClock(arrives) : null,
                    null))
                .ToList(),

            _ => [],
        };
    }

    private static void EnsureNoPlatformBrand(DocumentRenderModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Brand.Name) || model.PrintedText().Any(TravellerBrandGuard.MentionsPlatform))
        {
            throw new DocumentBrandException(
                $"Document {model.DocumentNumber} would carry the Trips brand to a traveller. Refusing to render it "
                + "(CLAUDE.md rule 4).");
        }
    }

    // ------------------------------------------------------------------ step 3: email

    private async Task EmailAsync(
        Order order,
        IReadOnlyList<GeneratedDocument> documents,
        string templateKey,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        var ready = documents.Where(d => d is { IsReady: true, AssetId: not null }).ToList();
        var address = ready.Select(d => d.RecipientEmail).FirstOrDefault(email => !string.IsNullOrWhiteSpace(email));

        if (ready.Count == 0 || address is null)
        {
            LogNotEmailed(_logger, order.Id);
            return;
        }

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                order.AgencyId,
                templateKey,
                address,
                ready[0].RecipientName ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = order.OrderNumber,
                    ["itinerarySummary"] = string.Join("; ", ConfirmedLines(order).Select(line => line.TitleSnapshot)),
                    ["documentList"] = string.Join(", ", ready.Select(Describe)),
                },
                dedupeKey,
                AttachmentAssetIds: ready.Select(d => d.AssetId!.Value).ToList()),
            cancellationToken);

        // Staged just now, or already there from an earlier run: either way, the email that carries them.
        var notificationId = _db.Notifications.Local
                                 .FirstOrDefault(n => n.AgencyId == order.AgencyId && n.DedupeKey == dedupeKey)?.Id
                             ?? await _db.Notifications
                                 .Where(n => n.AgencyId == order.AgencyId && n.DedupeKey == dedupeKey)
                                 .Select(n => (Guid?)n.Id)
                                 .FirstOrDefaultAsync(cancellationToken);

        if (notificationId is { } id)
        {
            foreach (var document in ready)
            {
                document.RecordEmail(id);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private void EnsureActingAs(Guid agencyId)
    {
        if (_tenant.AgencyId != agencyId)
        {
            throw new InvalidOperationException(
                $"Documents for agency {agencyId} must be issued acting as that agency, but this scope is acting as "
                + $"{_tenant.AgencyId?.ToString() ?? "no agency"}. The consumer sets the tenant from the message first.");
        }
    }

    private Task<Order?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    private static List<OrderLine> ConfirmedLines(Order order) =>
        order.Lines
            .Where(line => line.FulfilmentStatus == FulfilmentStatus.Confirmed)
            .OrderBy(line => line.Id)
            .ToList();

    private static string PaymentStatusFor(OrderStatus status) => status switch
    {
        OrderStatus.PendingPayment => "Awaiting payment",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Refunded => "Refunded",
        _ => "Paid",
    };

    private static string Describe(GeneratedDocument document) =>
        $"{(document.DocumentType == DocumentType.Invoice ? "invoice" : "voucher")} {document.DocumentNumber}";

    private static string FullName(string first, string? middle, string last) =>
        string.Join(' ', new[] { first, middle, last }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));

    /// <summary>"Fri 2 Oct 2026, 06:45", in the offset it was recorded with — the time on the ticket.</summary>
    private static string WallClock(DateTimeOffset at) => at.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static string? FlightDetail(FlightSegment segment)
    {
        var cabin = string.IsNullOrWhiteSpace(segment.Cabin)
            ? null
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segment.Cabin.Replace('_', ' ').ToLowerInvariant());

        var parts = new[] { cabin, segment.BaggageAllowance }.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The seats as the supplier listed them — a JSON array — written out plainly.</summary>
    private static string? Seats(string? seatNumbersJson)
    {
        if (string.IsNullOrWhiteSpace(seatNumbersJson))
        {
            return null;
        }

        try
        {
            using var parsed = JsonDocument.Parse(seatNumbersJson);

            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var seats = parsed.RootElement.EnumerateArray()
                .Select(seat => seat.ValueKind == JsonValueKind.String ? seat.GetString() : seat.GetRawText())
                .Where(seat => !string.IsNullOrWhiteSpace(seat))
                .ToList();

            return seats.Count == 0 ? null : string.Join(", ", seats);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RenderSources(
        DocumentBrand Brand,
        TimeZoneInfo TimeZone,
        IReadOnlyList<OrderTraveller> Travellers,
        IReadOnlyDictionary<Guid, SupplierBookingSnapshot> Bookings,
        IReadOnlyList<FlightSegment> Flights,
        IReadOnlyList<BusSegment> Buses,
        IReadOnlyDictionary<Guid, string> ReplacedNumbers);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Documents were asked for order {OrderId}, which agency {AgencyId} does not have. Nothing issued.")]
    private static partial void LogOrderNotFound(ILogger logger, Guid orderId, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Document {DocumentId} was asked to render, and agency {AgencyId} has no such order document.")]
    private static partial void LogDocumentNotFound(ILogger logger, Guid documentId, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order {OrderId} has no confirmed line yet, so it has no documents to issue.")]
    private static partial void LogNothingConfirmed(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Another run numbered order {OrderId}'s documents first; using those.")]
    private static partial void LogLostNumberingRace(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Rendered {DocumentNumber} ({DocumentId}) for agency {AgencyId}.")]
    private static partial void LogRendered(ILogger logger, string documentNumber, Guid documentId, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Rendering {DocumentNumber} ({DocumentId}) failed on attempt {Attempt}; it will be tried again.")]
    private static partial void LogWillRetry(ILogger logger, Exception exception, string documentNumber, Guid documentId, int attempt);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Rendering {DocumentNumber} ({DocumentId}) failed for the last time after {Attempts} attempts and is "
                  + "marked failed. It keeps its number; asking for the order's documents again renders it again.")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, string documentNumber, Guid documentId, int attempts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order {OrderId}'s documents are ready but were not emailed: there is no address to send them to.")]
    private static partial void LogNotEmailed(ILogger logger, Guid orderId);
}
