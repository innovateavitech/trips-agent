namespace TripsAgent.Application.Commerce;

/// <summary>One thing wrong with a request, and the field it belongs to.</summary>
/// <param name="Field">The request field, so the storefront can put the message beside its input.</param>
public sealed record StoreProblem(string Field, string Message);

/// <summary>
/// What came of a request from a traveller on an agency's storefront.
/// </summary>
/// <remarks>
/// <para>
/// A closed set, so the API maps each case to exactly one status: Done to 200 or 201, NotFound to
/// 404, Invalid to 422 with every problem keyed by its field, Refused to 409. The CRM's
/// <c>CrmResult</c> is the same shape for the same reason; they are kept apart because a cart and a
/// lead have nothing else in common, and merging them would tie the two modules together.
/// </para>
/// <para>
/// <b>NotFound is deliberately vague.</b> A host nobody's storefront answers on, a cart whose
/// session token is wrong, and another agency's product all produce the same 404 — so nobody
/// anonymous can map what exists on the platform by asking.
/// </para>
/// </remarks>
public abstract record StoreResult<T>
{
    private StoreResult()
    {
    }

    /// <summary>It was done. <paramref name="Value"/> is what to show.</summary>
    public sealed record Done(T Value) : StoreResult<T>;

    /// <summary>There is no such thing here, or none this caller could ever see.</summary>
    public sealed record NotFound(string Title) : StoreResult<T>;

    /// <summary>The request cannot be carried out as it is. <paramref name="Problems"/> says why.</summary>
    public sealed record Invalid(string Title, IReadOnlyList<StoreProblem> Problems) : StoreResult<T>;

    /// <summary>Well-formed, but the thing it asks for is not in a state to take it.</summary>
    public sealed record Refused(string Title, string Detail) : StoreResult<T>;
}

/// <summary>Shorthands for the results the commerce services return most.</summary>
internal static class Store
{
    /// <summary>One problem about one field.</summary>
    public static StoreResult<T> Invalid<T>(string title, string field, string message) =>
        new StoreResult<T>.Invalid(title, [new StoreProblem(field, message)]);

    /// <summary>The all-purpose 404. Says nothing about which of the possible reasons it was.</summary>
    public static StoreResult<T> NotFound<T>(string title = "We could not find that.") =>
        new StoreResult<T>.NotFound(title);
}
