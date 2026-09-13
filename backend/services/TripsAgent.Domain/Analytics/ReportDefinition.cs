using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Analytics;

/// <summary>
/// <c>analytics.report_definitions</c> — the catalogue of reports that can be run.
/// </summary>
/// <remarks>
/// <para>
/// Reference data, seeded by the migration and read by everybody, like the permission catalogue.
/// It carries no agency and no agency's data, so it has no tenant filter and no policy — there is
/// nothing in it to leak. What it does carry is the two facts the API refuses a request on: the
/// <see cref="Scope"/>, which decides whether the read crosses tenants, and the
/// <see cref="RequiredPermission"/>, which decides who may ask.
/// </para>
/// <para>
/// A table rather than a C# list because the console shows it: an agent picks a report by name
/// from a dropdown filled by the server, and a definition can be switched off
/// (<see cref="IsActive"/>) without a deployment when one turns out to be too expensive. The code
/// that actually produces the rows is still C#, keyed by <see cref="Code"/> — this table decides
/// what may be asked for, never how it is answered.
/// </para>
/// </remarks>
public sealed class ReportDefinition : Entity
{
    private ReportDefinition()
    {
        Code = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
        RequiredPermission = string.Empty;
    }

    public static ReportDefinition Create(
        Guid id,
        string code,
        string name,
        string description,
        ReportScope scope,
        string requiredPermission,
        bool isActive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredPermission);

        return new ReportDefinition
        {
            Id = id,
            Code = code,
            Name = name,
            Description = description,
            Scope = scope,
            RequiredPermission = requiredPermission,
            IsActive = isActive,
        };
    }

    /// <summary>A stable machine name, e.g. <c>agency.sales.daily</c>. Unique.</summary>
    public string Code { get; private set; }

    /// <summary>What it is called on screen.</summary>
    public string Name { get; private set; }

    /// <summary>One sentence saying what the rows are.</summary>
    public string Description { get; private set; }

    /// <summary>Whether running it crosses agencies.</summary>
    public ReportScope Scope { get; private set; }

    /// <summary>The permission code the caller must hold. See <c>PermissionCodes</c>.</summary>
    public string RequiredPermission { get; private set; }

    /// <summary>False takes it off the menu without removing the rows already exported from it.</summary>
    public bool IsActive { get; private set; }
}

/// <summary>
/// When a report has to be run in the background rather than in the request.
/// </summary>
/// <remarks>
/// <para>
/// Two conditions, from FRD §2.15 UC-1C, and either one is enough:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>More than 90 days.</b> A year of a busy agency's lines is tens of thousands of rows and
///     an HTTP request is the wrong place to build it: the caller's browser times out, the
///     connection is held open, and a retry starts the whole thing again.
///   </item>
///   <item>
///     <b>Cross-tenant.</b> A platform report reads every agency at once. It is unbounded by
///     definition — it grows with the business, not with the request — and it is the read the FRD
///     is most careful about, so it never runs in a request where a network hiccup makes it
///     ambiguous whether it ran at all.
///   </item>
/// </list>
/// <para>
/// The rule is a pure function so it can be tested without a database, and so the console can
/// tell an agent <i>before</i> they press the button that this one will arrive by email.
/// </para>
/// </remarks>
public static class ReportScopeRules
{
    /// <summary>The longest window a report may cover and still run in the request.</summary>
    public const int MaximumSynchronousDays = 90;

    /// <summary>
    /// How a report covering <paramref name="from"/> to <paramref name="to"/> inclusive must run.
    /// </summary>
    public static ReportRunMode ModeFor(ReportScope scope, DateOnly from, DateOnly to) =>
        scope == ReportScope.Platform || DaysCovered(from, to) > MaximumSynchronousDays
            ? ReportRunMode.Asynchronous
            : ReportRunMode.Synchronous;

    /// <summary>Days in an inclusive range. One day is one day, not zero.</summary>
    public static int DaysCovered(DateOnly from, DateOnly to) =>
        to < from ? 0 : to.DayNumber - from.DayNumber + 1;
}
