using System.Net;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// Issuing a ticket, and asking where a booking has got to. Flights and buses share one issue endpoint
/// and the same answers, so both adapters share this (#36, #37).
/// </summary>
/// <remarks>
/// <para>
/// <b>The issue call is sent once, and only once.</b> There is no loop in here, and no retry handler can
/// sit underneath: <see cref="TripsAfricaIssueHttp"/> is built by <c>AddSupplierHttpClient</c>, which refuses
/// one. Every failure that could have happened after the request left — a timeout, a dropped connection,
/// a 5xx, an answer that cannot be read — comes back as <see cref="SupplierIssueOutcome.Unknown"/>, never
/// as an exception inviting a second try. See docs/adr/0003-never-retry-ticket-issuance.md.
/// </para>
/// <para>
/// <b>The status query is a read</b>, safe to ask as often as needed, and it is how every uncertain issue
/// outcome is settled.
/// </para>
/// </remarks>
public sealed class TripsAfricaTicketing
{
    internal const string IssuePath = "api/v2/ticketing/issue";
    internal const string StatusPath = "api/Flight/GetBookingStatus";
    internal const string BusReservationPath = "api/Bus/MyReservation";

    private readonly TripsAfricaIssueHttp _issue;
    private readonly TripsAfricaBookingHttp _booking;
    private readonly TripsAfricaCredentials _credentials;
    private readonly TripsAfricaSupplier _supplier;

    public TripsAfricaTicketing(
        TripsAfricaIssueHttp issue,
        TripsAfricaBookingHttp booking,
        TripsAfricaCredentials credentials,
        TripsAfricaSupplier supplier)
    {
        _issue = issue;
        _booking = booking;
        _credentials = credentials;
        _supplier = supplier;
    }

    /// <summary>Sends the issue call. See the remarks on the class before changing anything here.</summary>
    public async Task<SupplierIssueResult> IssueAsync(
        SupplierProductType product,
        SupplierCallContext context,
        SupplierIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        // Everything that can fail before a request exists happens first, so a failure here sent nothing.
        var body = TripsAfricaMapping.ToIssueBody(request, product);
        var credentials = await _credentials.ForAsync(context, cancellationToken);
        var supplierId = await _supplier.IdAsync(cancellationToken);

        TripsAfricaResponse response;

        try
        {
            // One attempt. docs/adr/0003-never-retry-ticket-issuance.md
            response = await _issue.PostAsync(
                IssuePath, body, product, credentials, supplierId, SupplierOperation.Issue, context, cancellationToken);
        }
        catch (SupplierCallOutcomeUnknownException ex)
        {
            // A timeout, or a connection lost after the request left: a ticket may exist.
            return Unknown(httpStatusCode: null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            // Provably never left — and still not sent again from here. One recovery path for every
            // failure: the status query, which will find nothing issued.
            return Unknown(httpStatusCode: null, $"The issue request never reached Trips Africa ({ex.HttpRequestError}): {ex.Message}");
        }

        var status = (int)response.StatusCode;

        if (response.IsClientError)
        {
            TripsAfricaMapping.TryReadIssueAnswer(response.Body, out var refused);

            return new SupplierIssueResult(
                SupplierIssueOutcome.Rejected,
                status,
                refused.StatusCode,
                TripsAfricaMapping.StatusFor(refused.StatusCode),
                refused.Pnr,
                refused.Message ?? $"HTTP {status}");
        }

        if (!response.IsSuccess)
        {
            return Unknown(status, $"Trips Africa answered the issue call with HTTP {status}. A ticket may still have been issued.");
        }

        if (!TripsAfricaMapping.TryReadIssueAnswer(response.Body, out var answer))
        {
            return Unknown(status, "Trips Africa's answer to the issue call could not be read. A ticket may still have been issued.");
        }

        return new SupplierIssueResult(
            answer.IsSuccessful ? SupplierIssueOutcome.Accepted : SupplierIssueOutcome.Rejected,
            status,
            answer.StatusCode,
            TripsAfricaMapping.StatusFor(answer.StatusCode),
            answer.Pnr,
            answer.Message);
    }

    /// <summary>Asks where a booking has got to.</summary>
    /// <remarks>
    /// A bus booking with a PNR is looked up where the documentation says, <c>Bus/MyReservation</c>.
    /// Without a PNR — an issue call that timed out — the flight status query is the only documented lookup
    /// by confirmation code; whether it knows bus bookings is not documented, and is to be checked on staging.
    /// </remarks>
    public async Task<SupplierStatusResult> GetStatusAsync(
        SupplierProductType product,
        SupplierCallContext context,
        SupplierStatusQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.PassengerSurname))
        {
            throw new ArgumentException(
                "Trips Africa finds a booking by its reference and the lead passenger's surname, and there is no surname to send.",
                nameof(query));
        }

        var busReservation = product == SupplierProductType.Bus && !string.IsNullOrWhiteSpace(query.Pnr);

        var (path, body) = busReservation
            ? (BusReservationPath, TripsAfricaMapping.ToBusReservationBody(query.Pnr!, query.PassengerSurname))
            : (StatusPath, TripsAfricaMapping.ToStatusBody(query.ConfirmationCode, query.PassengerSurname));

        var credentials = await _credentials.ForAsync(context, cancellationToken);
        var supplierId = await _supplier.IdAsync(cancellationToken);

        TripsAfricaResponse response;

        try
        {
            response = await _booking.PostAsync(
                path, body, product, credentials, supplierId, SupplierOperation.Status, context, cancellationToken);
        }
        catch (SupplierCallOutcomeUnknownException ex)
        {
            return NoAnswer(SupplierPollOutcome.Timeout, httpStatusCode: null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return NoAnswer(SupplierPollOutcome.HttpError, httpStatusCode: null, $"{ex.HttpRequestError}: {ex.Message}");
        }

        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NoAnswer(SupplierPollOutcome.NotFound, status, "Trips Africa does not know the booking.", response.AuditedCallId);
        }

        if (!response.IsSuccess)
        {
            return NoAnswer(SupplierPollOutcome.HttpError, status, $"Trips Africa answered the status query with HTTP {status}.", response.AuditedCallId);
        }

        int? code;
        string? message;
        var pnr = query.Pnr;
        bool read;

        if (busReservation)
        {
            read = TripsAfricaMapping.TryReadBusReservation(response.Body, out code, out var reservationPnr, out message);
            pnr = reservationPnr ?? pnr;
        }
        else
        {
            read = TripsAfricaMapping.TryReadBookingStatus(response.Body, out code, out message);
        }

        if (!read)
        {
            return NoAnswer(SupplierPollOutcome.HttpError, status, "Trips Africa's answer to the status query could not be read.", response.AuditedCallId);
        }

        return new SupplierStatusResult(
            SupplierPollOutcome.Answered,
            status,
            code,
            TripsAfricaMapping.StatusFor(code),
            pnr,
            Tickets: [],
            message,
            response.AuditedCallId);
    }

    private static SupplierIssueResult Unknown(int? httpStatusCode, string message) =>
        new(SupplierIssueOutcome.Unknown, httpStatusCode, SupplierStatusCode: null, Status: null, Pnr: null, message);

    private static SupplierStatusResult NoAnswer(
        SupplierPollOutcome outcome,
        int? httpStatusCode,
        string message,
        Guid? auditedCallId = null) =>
        new(outcome, httpStatusCode, SupplierStatusCode: null, Status: null, Pnr: null, Tickets: [], message, auditedCallId);
}
