using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// A supplier call got no response in time. <b>The outcome is unknown, not failed.</b>
/// </summary>
/// <remarks>
/// <para>
/// The request may have reached the supplier and been acted on; only the answer was lost. For the
/// ticket-issue call that means a ticket may exist. ADR-0003: resolve it by polling the booking's
/// status, never by sending the call again — the issue endpoint has no idempotency key, so a second
/// call issues a second real ticket.
/// </para>
/// <para>
/// A <see cref="TimeoutException"/> rather than the <see cref="TaskCanceledException"/> a plain
/// <see cref="HttpClient"/> throws, so an adapter can tell "the supplier went quiet" apart from
/// "our caller gave up" without inspecting inner exceptions.
/// </para>
/// </remarks>
public sealed class SupplierCallTimeoutException : TimeoutException
{
    public SupplierCallTimeoutException()
    {
    }

    public SupplierCallTimeoutException(string message)
        : base(message)
    {
    }

    public SupplierCallTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SupplierCallTimeoutException(SupplierOperation operation, TimeSpan timeout, Exception innerException)
        : base(
            $"The supplier did not answer the {operation} call within {timeout.TotalSeconds:0.#} seconds. "
            + "The outcome is unknown: resolve it by polling the booking's status, never by sending "
            + "the call again (ADR-0003).",
            innerException)
    {
        Operation = operation;
    }

    /// <summary>Which call timed out.</summary>
    public SupplierOperation? Operation { get; }
}
