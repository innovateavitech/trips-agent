namespace TripsAgent.Application.Suppliers;

/// <summary>
/// A supplier could not be asked: every search attempt failed before an answer came back, or its
/// circuit breaker is open after repeated failures. <b>Nothing was bought.</b>
/// </summary>
/// <remarks>
/// Only ever thrown for reads — searches and fare rules — which are safe to try again. Booking calls
/// never reach this: their failures are unknown outcomes (<see cref="SupplierCallOutcomeUnknownException"/>),
/// resolved by polling, never by trying again (ADR-0003).
/// </remarks>
public sealed class SupplierUnavailableException : Exception
{
    public SupplierUnavailableException()
    {
    }

    public SupplierUnavailableException(string message)
        : base(message)
    {
    }

    public SupplierUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The supplier answered and refused the request — an HTTP 4xx. Trying again sends the same request
/// and gets the same answer, so nothing retries this.
/// </summary>
public sealed class SupplierRequestRejectedException : Exception
{
    public SupplierRequestRejectedException()
    {
    }

    public SupplierRequestRejectedException(string message)
        : base(message)
    {
    }

    public SupplierRequestRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SupplierRequestRejectedException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The supplier's HTTP status code.</summary>
    public int? StatusCode { get; }
}
