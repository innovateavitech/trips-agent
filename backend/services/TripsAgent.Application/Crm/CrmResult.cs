using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>What came of a CRM request that changes something.</summary>
/// <remarks>
/// A closed set, so the API maps each case to exactly one status: Done to 200 or 201, NotFound to
/// 404, Invalid to 422 with every problem keyed by its field, Refused to 409.
/// </remarks>
public abstract record CrmResult<T>
{
    private CrmResult()
    {
    }

    /// <summary>It was done. <paramref name="Value"/> is the record as it now stands.</summary>
    public sealed record Done(T Value) : CrmResult<T>;

    /// <summary>There is no such record in this agency. Another agency's looks exactly like a missing one.</summary>
    public sealed record NotFound(string Title) : CrmResult<T>;

    /// <summary>The request cannot be saved as it is. <paramref name="Problems"/> lists every reason.</summary>
    public sealed record Invalid(string Title, IReadOnlyList<CrmProblem> Problems) : CrmResult<T>;

    /// <summary>The request was well-formed, but the record is not in a state to take it.</summary>
    public sealed record Refused(string Title, string Detail) : CrmResult<T>;
}
