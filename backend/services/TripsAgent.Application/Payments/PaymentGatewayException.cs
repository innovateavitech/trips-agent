namespace TripsAgent.Application.Payments;

/// <summary>
/// The gateway gave us an answer we cannot act on: a refusal, or a response we could not read.
/// </summary>
/// <remarks>
/// <para>
/// Thrown instead of the HTTP or JSON exception underneath, so code above the gateway can tell
/// "the gateway said no, or said something strange" apart from a bug of ours — without
/// referencing <c>System.Net.Http</c> or the gateway's wire format.
/// </para>
/// <para>
/// Retrying may not help, so the webhook path counts it against the event's attempts. It never
/// means the payment failed: a payer can be charged even when the verify call breaks, so the
/// payment is left pending rather than marked failed.
/// </para>
/// </remarks>
public class PaymentGatewayException : Exception
{
    public PaymentGatewayException()
    {
    }

    public PaymentGatewayException(string message)
        : base(message)
    {
    }

    public PaymentGatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The gateway could not be asked at all: it timed out, returned a 5xx, or the circuit breaker is
/// open.
/// </summary>
/// <remarks>
/// Always worth retrying later. The webhook path backs off without using up the event's attempts
/// for these, because a gateway outage of a few minutes must not dead-letter every payment that
/// arrived during it.
/// </remarks>
public sealed class PaymentGatewayUnavailableException : PaymentGatewayException
{
    public PaymentGatewayUnavailableException()
    {
    }

    public PaymentGatewayUnavailableException(string message)
        : base(message)
    {
    }

    public PaymentGatewayUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
