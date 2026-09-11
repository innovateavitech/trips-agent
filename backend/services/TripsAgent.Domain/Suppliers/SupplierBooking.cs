using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>One price confirmation, as the supplier returned it.</summary>
/// <param name="HashExpected">The hash we computed from the merchant key, the code and the new price.</param>
/// <param name="HashReceived">The hash the supplier sent.</param>
public sealed record PriceConfirmationLine(
    string ConfirmationCode,
    Money OldPrice,
    Money NewPrice,
    DateTimeOffset? TicketTimeLimit,
    string HashExpected,
    string HashReceived);

/// <summary>
/// Our record of one booking with a supplier — the heart of the integration.
/// </summary>
/// <remarks>
/// <para>
/// One per order line, enforced by <c>UNIQUE (order_line_id)</c>. That constraint is the layer of
/// double-ticketing protection that survives a process dying (ADR-0003, plan §4): a second worker
/// that somehow reaches this point for the same line cannot even create the row.
/// </para>
/// <para>
/// A domestic or round-trip price confirmation returns an <b>array</b>, each element with its own
/// confirmation code, price and hash. Each is kept as a <see cref="SupplierBookingConfirmation"/>
/// and verified on its own; the booking is only ready to issue when every one verified. This is
/// the plan's recommendation for open question 22 — one order line, N confirmations — and it
/// also holds if the answer turns out to be N order lines, each with one confirmation.
/// </para>
/// <para>
/// Only the transitions this issue needs are here: creation, price confirmation, and the guard on
/// entering <see cref="SupplierBookingStatus.Issuing"/>. Issuance (#36) and polling (#37) add theirs.
/// </para>
/// </remarks>
public sealed class SupplierBooking : Entity, IAuditableEntity, ITenantScoped
{
    private readonly List<SupplierBookingConfirmation> _confirmations = [];

    private SupplierBooking()
    {
        SupplierSessionId = string.Empty;
        Currency = string.Empty;
        IdempotencyKey = string.Empty;
    }

    /// <param name="tripType">The supplier's own trip-type word, sent back on issue — Trips Africa's "International" or "Domestic".</param>
    /// <param name="tripMode">The supplier's own mode word — Trips Africa's "Flight" or "Road".</param>
    /// <param name="idempotencyKey">Unique platform-wide. Derived from the order line, so a retried saga step lands on the same row.</param>
    public static SupplierBooking Create(
        Guid agencyId,
        Guid supplierId,
        Guid orderLineId,
        Guid? supplierOfferId,
        SupplierProductType productType,
        string? tripType,
        string? tripMode,
        string supplierSessionId,
        string currency,
        string idempotencyKey)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return new SupplierBooking
        {
            AgencyId = agencyId,
            SupplierId = supplierId,
            OrderLineId = orderLineId,
            SupplierOfferId = supplierOfferId,
            ProductType = productType,
            TripType = tripType,
            TripMode = tripMode,
            SupplierSessionId = supplierSessionId,
            Currency = currency.Trim().ToUpperInvariant(),
            IdempotencyKey = idempotencyKey,
            Status = SupplierBookingStatus.PendingConfirmation,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierId { get; private set; }

    /// <summary>
    /// The order line this fulfils. Unique. The foreign key to <c>orders.order_lines</c> arrives
    /// with that table (#41).
    /// </summary>
    public Guid OrderLineId { get; private set; }

    public Guid? SupplierOfferId { get; private set; }

    public SupplierProductType ProductType { get; private set; }

    public string? TripType { get; private set; }

    public string? TripMode { get; private set; }

    public string SupplierSessionId { get; private set; }

    /// <summary>The first confirmation's code — what a status query quotes back.</summary>
    public string? ConfirmationCode { get; private set; }

    public string? Pnr { get; private set; }

    /// <summary>The earliest ticket time limit across every confirmation. Issue after it and the booking is dead.</summary>
    public DateTimeOffset? TicketTimeLimit { get; private set; }

    public SupplierBookingStatus Status { get; private set; }

    /// <summary>The supplier's own status code, verbatim. Trips Africa: 0, 1, 2, 3, 11 or 100.</summary>
    public int? SupplierStatusCode { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The total across every confirmation, as priced at search time.</summary>
    public Money? OldPriceMinor { get; private set; }

    /// <summary>The total across every confirmation, as confirmed. This is what we pay the supplier.</summary>
    public Money? NewPriceMinor { get; private set; }

    /// <summary>True when the confirmed price differs from the searched one — the customer must consent again.</summary>
    public bool PriceChanged { get; private set; }

    /// <summary>True only when every confirmation's hash verified.</summary>
    public bool HashVerified { get; private set; }

    public string IdempotencyKey { get; private set; }

    public DateTimeOffset? IssueStartedAt { get; private set; }

    public DateTimeOffset? LastPolledAt { get; private set; }

    public int PollAttempts { get; private set; }

    /// <summary>When the poller should next ask the supplier. With <see cref="Status"/>, it drives the poller's index.</summary>
    public DateTimeOffset? NextPollAt { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<SupplierBookingConfirmation> Confirmations => _confirmations;

    /// <summary>
    /// Records what the supplier confirmed, verifying each element's hash on its own.
    /// </summary>
    /// <remarks>
    /// One failed hash rejects the whole booking. Accepting the elements that verified would issue
    /// part of a journey at a price nobody vouched for.
    /// </remarks>
    public void RecordPriceConfirmation(IReadOnlyList<PriceConfirmationLine> lines, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            // An empty array would otherwise "verify" vacuously — every one of zero hashes matched.
            throw new ArgumentException("A price confirmation must contain at least one element.", nameof(lines));
        }

        if (Status is not (SupplierBookingStatus.PendingConfirmation or SupplierBookingStatus.PriceConfirmed))
        {
            throw new InvalidOperationException(
                $"Booking {Id} is {Status}; its price can no longer be confirmed. Once issuing has begun the price is frozen.");
        }

        _confirmations.Clear();

        for (var index = 0; index < lines.Count; index++)
        {
            _confirmations.Add(SupplierBookingConfirmation.From(AgencyId, Id, index, lines[index]));
        }

        ConfirmationCode = lines[0].ConfirmationCode;
        OldPriceMinor = new Money(lines.Sum(line => line.OldPrice.AmountMinor));
        NewPriceMinor = new Money(lines.Sum(line => line.NewPrice.AmountMinor));
        PriceChanged = lines.Any(line => line.OldPrice != line.NewPrice);
        TicketTimeLimit = lines.Min(line => line.TicketTimeLimit);
        HashVerified = _confirmations.All(confirmation => confirmation.HashVerified);

        if (HashVerified)
        {
            Status = SupplierBookingStatus.PriceConfirmed;
            FailureReason = null;
        }
        else
        {
            Status = SupplierBookingStatus.PriceRejected;
            FailureReason = $"Price confirmation hash mismatch at {at:O}.";
        }
    }

    /// <summary>
    /// Moves to <see cref="SupplierBookingStatus.Issuing"/> — the last step before the issue call.
    /// </summary>
    /// <remarks>
    /// Allowed exactly once, from a verified price confirmation. A booking that is already issuing,
    /// or whose issue outcome is unknown, can never come back here: per ADR-0003 an unknown outcome
    /// is resolved by polling the supplier, and re-entering this state is how a second real ticket
    /// gets issued.
    /// </remarks>
    public void BeginIssue(DateTimeOffset at)
    {
        if (Status != SupplierBookingStatus.PriceConfirmed)
        {
            throw new InvalidOperationException(
                $"Booking {Id} is {Status}; only a booking whose price was confirmed may start issuing, and only once. "
                + "Never retry ticket issuance — see docs/adr/0003-never-retry-ticket-issuance.md.");
        }

        if (!HashVerified)
        {
            throw new InvalidOperationException($"Booking {Id} has an unverified price confirmation and cannot be issued.");
        }

        if (TicketTimeLimit is { } limit && at >= limit)
        {
            throw new InvalidOperationException($"Booking {Id} passed its ticket time limit at {limit:O}; it can no longer be issued.");
        }

        Status = SupplierBookingStatus.Issuing;
        IssueStartedAt = at;
    }
}

/// <summary>
/// One element of a supplier's price confirmation, with its own hash check.
/// </summary>
public sealed class SupplierBookingConfirmation : Entity, ITenantScoped
{
    private SupplierBookingConfirmation()
    {
        ConfirmationCode = string.Empty;
        HashExpected = string.Empty;
        HashReceived = string.Empty;
    }

    internal static SupplierBookingConfirmation From(Guid agencyId, Guid bookingId, int sequence, PriceConfirmationLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.ConfirmationCode);

        if (line.OldPrice.IsNegative || line.NewPrice.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "A confirmed price cannot be negative.");
        }

        return new SupplierBookingConfirmation
        {
            AgencyId = agencyId,
            SupplierBookingId = bookingId,
            Sequence = sequence,
            ConfirmationCode = line.ConfirmationCode,
            OldPriceMinor = line.OldPrice,
            NewPriceMinor = line.NewPrice,
            TicketTimeLimit = line.TicketTimeLimit,
            HashExpected = line.HashExpected ?? string.Empty,
            HashReceived = line.HashReceived ?? string.Empty,
            HashVerified = HashesMatch(line.HashExpected, line.HashReceived),
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierBookingId { get; private set; }

    /// <summary>Position in the supplier's array, from zero.</summary>
    public int Sequence { get; private set; }

    public string ConfirmationCode { get; private set; }

    public Money OldPriceMinor { get; private set; }

    public Money NewPriceMinor { get; private set; }

    public DateTimeOffset? TicketTimeLimit { get; private set; }

    public string HashExpected { get; private set; }

    public string HashReceived { get; private set; }

    /// <summary>Derived from the two hashes, never passed in, so it cannot disagree with them.</summary>
    public bool HashVerified { get; private set; }

    /// <summary>
    /// Hex digests compared case-insensitively. Neither side may be empty — two empty strings are
    /// equal, and "the supplier sent no hash" must never read as "the hash matched".
    /// </summary>
    public static bool HashesMatch(string? expected, string? received) =>
        !string.IsNullOrWhiteSpace(expected)
        && !string.IsNullOrWhiteSpace(received)
        && string.Equals(expected.Trim(), received.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A traveller on a supplier booking.</summary>
public sealed class SupplierBookingPassenger : Entity, ITenantScoped
{
    private SupplierBookingPassenger()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    public static SupplierBookingPassenger Add(
        Guid agencyId,
        Guid supplierBookingId,
        PassengerType passengerType,
        string firstName,
        string lastName,
        string? middleName = null,
        string? title = null,
        DateOnly? birthDate = null,
        string? gender = null,
        string? email = null,
        string? phoneNumber = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierBookingId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        return new SupplierBookingPassenger
        {
            AgencyId = agencyId,
            SupplierBookingId = supplierBookingId,
            PassengerType = passengerType,
            FirstName = firstName.Trim(),
            MiddleName = middleName?.Trim(),
            LastName = lastName.Trim(),
            Title = title,
            BirthDate = birthDate,
            Gender = gender,
            Email = email,
            PhoneNumber = phoneNumber,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierBookingId { get; private set; }

    public PassengerType PassengerType { get; private set; }

    public string? Title { get; private set; }

    public string FirstName { get; private set; }

    public string? MiddleName { get; private set; }

    /// <summary>With the confirmation code, what a status query identifies the booking by.</summary>
    public string LastName { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    public string? Gender { get; private set; }

    public string? Email { get; private set; }

    public string? PhoneNumber { get; private set; }

    /// <summary>JSON array.</summary>
    public string? SeatNumbers { get; private set; }

    public string? TicketNumber { get; private set; }

    public void AssignSeats(string seatNumbersJson) => SeatNumbers = seatNumbersJson;

    public void RecordTicketNumber(string ticketNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketNumber);
        TicketNumber = ticketNumber;
    }
}

/// <summary>
/// A passenger's travel document. The number is personal data and is held only as ciphertext.
/// </summary>
/// <remarks>
/// Retention and erasure are #105's; the broader PII-at-rest policy is #104's. What this issue
/// guarantees is that the number never reaches the database in clear.
/// </remarks>
public sealed class PassengerDocument : Entity, IAuditableEntity, ITenantScoped
{
    private PassengerDocument()
    {
        DocNumberEncrypted = [];
        IssuingCountry = string.Empty;
    }

    public static PassengerDocument Add(
        Guid agencyId,
        Guid passengerId,
        TravelDocumentRecord docType,
        TravelDocumentKind innerDocType,
        byte[] docNumberEncrypted,
        string issuingCountry,
        string? nationalityCountry = null,
        DateOnly? issuedOn = null,
        DateOnly? expiresOn = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(passengerId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(docNumberEncrypted);

        if (docNumberEncrypted.Length == 0)
        {
            throw new ArgumentException("The encrypted document number is empty.", nameof(docNumberEncrypted));
        }

        if (issuedOn is { } issued && expiresOn is { } expires && expires < issued)
        {
            throw new ArgumentException("A document cannot expire before it was issued.", nameof(expiresOn));
        }

        return new PassengerDocument
        {
            AgencyId = agencyId,
            PassengerId = passengerId,
            DocType = docType,
            InnerDocType = innerDocType,
            DocNumberEncrypted = docNumberEncrypted,
            IssuingCountry = Country(issuingCountry, nameof(issuingCountry)),
            NationalityCountry = nationalityCountry is null ? null : Country(nationalityCountry, nameof(nationalityCountry)),
            IssuedOn = issuedOn,
            ExpiresOn = expiresOn,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid PassengerId { get; private set; }

    public TravelDocumentRecord DocType { get; private set; }

    public TravelDocumentKind InnerDocType { get; private set; }

    public byte[] DocNumberEncrypted { get; private set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string IssuingCountry { get; private set; }

    public string? NationalityCountry { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    private static string Country(string code, string parameter)
    {
        var trimmed = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (trimmed.Length != 2 || !trimmed.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{code}' is not an ISO 3166-1 alpha-2 country code.", parameter);
        }

        return trimmed;
    }
}
