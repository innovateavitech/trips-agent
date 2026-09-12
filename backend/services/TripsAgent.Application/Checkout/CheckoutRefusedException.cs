namespace TripsAgent.Application.Checkout;

/// <summary>What kind of "no" a checkout step gave — which the API turns into a status code.</summary>
public enum CheckoutRefusal
{
    /// <summary>No such booking or fare for this agency. 404.</summary>
    NotFound = 1,

    /// <summary>It can no longer happen as asked: already paid, the price moved, already resolved. 409.</summary>
    Conflict = 2,

    /// <summary>The supplier's hold on the fare has ended. 410.</summary>
    Gone = 3,

    /// <summary>Understood, but not possible for this agency now: no wallet, not enough in it. 422.</summary>
    Unprocessable = 4,

    /// <summary>The supplier failed to confirm or its answer could not be trusted. Nothing was charged. 502.</summary>
    SupplierFailed = 5,
}

/// <summary>
/// A checkout step that cannot happen, with the reason in words an agent can act on. Nothing was
/// charged when this is thrown: every one is raised before money or a ticket moves.
/// </summary>
public sealed class CheckoutRefusedException : Exception
{
    public CheckoutRefusedException()
        : this(CheckoutRefusal.Conflict, "The booking could not go ahead.", "Nothing was booked or charged.")
    {
    }

    public CheckoutRefusedException(string message)
        : this(CheckoutRefusal.Conflict, message, "Nothing was booked or charged.")
    {
    }

    public CheckoutRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusal = CheckoutRefusal.Conflict;
        Title = message;
        Detail = "Nothing was booked or charged.";
    }

    public CheckoutRefusedException(CheckoutRefusal refusal, string title, string detail)
        : base($"{title} {detail}")
    {
        Refusal = refusal;
        Title = title;
        Detail = detail;
    }

    public CheckoutRefusal Refusal { get; }

    /// <summary>One line, for the heading of the error.</summary>
    public string Title { get; }

    /// <summary>What happened to the money, and what to do next.</summary>
    public string Detail { get; }
}
