using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// A supplier call may have reached the supplier, but no complete answer came back. <b>The outcome
/// is unknown, not failed.</b>
/// </summary>
/// <remarks>
/// <para>
/// For the ticket-issue call that means a ticket may exist. ADR-0003: resolve it by polling the
/// booking's status, never by sending the call again — the issue endpoint has no idempotency key,
/// so a second call issues a second real ticket.
/// </para>
/// <para>
/// <b>Catch this type to handle every unknown outcome at once.</b> A timeout arrives as
/// <see cref="SupplierCallTimeoutException"/>, which derives from this. A connection that dropped
/// after the request was sent arrives as this type itself, with the transport's exception inside.
/// A plain <see cref="HttpRequestException"/> from a supplier client means the request provably
/// never left — see <see cref="SupplierCallOutcome.TransportError"/>.
/// </para>
/// </remarks>
public class SupplierCallOutcomeUnknownException : Exception
{
    public SupplierCallOutcomeUnknownException()
    {
    }

    public SupplierCallOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public SupplierCallOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SupplierCallOutcomeUnknownException(SupplierOperation operation, Exception innerException)
        : this(
            operation,
            $"The {operation} call failed after the request may have reached the supplier, and no complete "
            + "answer came back. The outcome is unknown: resolve it by polling the booking's status, never "
            + "by sending the call again (ADR-0003).",
            innerException)
    {
    }

    protected SupplierCallOutcomeUnknownException(SupplierOperation operation, string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
    }

    /// <summary>Which call's outcome is unknown.</summary>
    public SupplierOperation? Operation { get; }
}
