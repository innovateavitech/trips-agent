using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Crm;

/// <summary>What the customer asked for: where, when, who is going, and roughly what they will spend.</summary>
/// <param name="BudgetMinMinor">The lowest figure they gave, in minor units. Null when they gave none.</param>
/// <param name="BudgetMaxMinor">The highest figure they gave, in minor units. Null when they gave none.</param>
public sealed record LeadDetails(
    string Destination,
    DateOnly? TravelFrom,
    DateOnly? TravelTo,
    int Adults,
    int Children,
    Money? BudgetMinMinor,
    Money? BudgetMaxMinor,
    string Message)
{
    /// <summary>The same details with text trimmed, so blank means blank.</summary>
    public LeadDetails Normalised() => this with
    {
        Destination = (Destination ?? string.Empty).Trim(),
        Message = (Message ?? string.Empty).Trim(),
    };
}

/// <summary>What an inquiry must say before it can be saved.</summary>
public static class LeadRules
{
    /// <summary>Every reason <paramref name="details"/> cannot be saved. Empty when it can.</summary>
    /// <remarks>Pass normalised details (<see cref="LeadDetails.Normalised"/>).</remarks>
    public static IReadOnlyList<CrmProblem> Validate(LeadDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var problems = new List<CrmProblem>();

        if (details.Destination.Length == 0)
        {
            problems.Add(new("destination", "Say where they want to go."));
        }
        else if (details.Destination.Length > CrmLimits.MaxDestinationLength)
        {
            problems.Add(new("destination", Limit("Keep the destination to {0} characters.", CrmLimits.MaxDestinationLength)));
        }

        if (details.Adults is < 1 or > CrmLimits.MaxPartySize)
        {
            problems.Add(new("adults", Limit("At least one adult, and no more than {0}.", CrmLimits.MaxPartySize)));
        }

        if (details.Children is < 0 or > CrmLimits.MaxPartySize)
        {
            problems.Add(new("children", Limit("A whole number from 0 to {0}.", CrmLimits.MaxPartySize)));
        }

        // An open end on either side is fine; two real dates must run forwards.
        if (details.TravelFrom is { } from && details.TravelTo is { } to && to < from)
        {
            problems.Add(new("travelTo", "The trip cannot end before it starts."));
        }

        CheckBudget(details.BudgetMinMinor, "budgetMinMinor", problems);
        CheckBudget(details.BudgetMaxMinor, "budgetMaxMinor", problems);

        if (details.BudgetMinMinor is { } min && details.BudgetMaxMinor is { } max && max < min)
        {
            problems.Add(new("budgetMaxMinor", "The highest figure is less than the lowest."));
        }

        if (details.Message.Length > CrmLimits.MaxMessageLength)
        {
            problems.Add(new("message", Limit("Keep the message to {0} characters.", CrmLimits.MaxMessageLength)));
        }

        return problems;
    }

    /// <summary>Every reason a move to <paramref name="stage"/> cannot be made as asked.</summary>
    public static IReadOnlyList<CrmProblem> CheckMove(LeadStage stage, string? reason)
    {
        var problems = new List<CrmProblem>();

        if (!Enum.IsDefined(stage))
        {
            problems.Add(new("stage", "Choose New, Quoted, Negotiating, Won or Lost."));
        }
        else if (stage == LeadStage.Lost && string.IsNullOrWhiteSpace(reason))
        {
            // The one stage that needs words: a lost lead is what the agency learns from.
            problems.Add(new("reason", "Say why it was lost. It is how the agency learns what to change."));
        }

        if (reason is not null && reason.Trim().Length > CrmLimits.MaxReasonLength)
        {
            problems.Add(new("reason", Limit("Keep the reason to {0} characters.", CrmLimits.MaxReasonLength)));
        }

        return problems;
    }

    private static void CheckBudget(Money? budget, string field, List<CrmProblem> problems)
    {
        if (budget is { } amount && (amount.IsNegative || amount.AmountMinor > CrmLimits.MaxAmountMinor))
        {
            problems.Add(new(field, "A budget is zero or more, in kobo, and below ₦100 billion."));
        }
    }

    private static string Limit(string format, int limit) =>
        string.Format(CultureInfo.InvariantCulture, format, limit);
}

/// <summary>
/// An inquiry from a customer, and where it has got to: New, Quoted, Negotiating, then Won or Lost.
/// </summary>
/// <remarks>
/// <para>
/// Every change of stage is written to <see cref="History"/> — who moved it, when, and for a lost lead
/// why — and the history is append-only: the application role may insert into it, never change it.
/// </para>
/// <para>
/// Any stage can follow any other, because agents fix mistakes and lost customers come back. Two
/// rules hold: a lead is never moved to the stage it is already at, and it is never lost without a
/// reason.
/// </para>
/// </remarks>
public sealed class Lead : Entity, IAuditableEntity, ITenantScoped
{
    private readonly List<LeadStageChange> _history = [];

    private Lead()
    {
        Destination = string.Empty;
        Currency = string.Empty;
        Message = string.Empty;
    }

    /// <summary>A new inquiry, at stage New. Throws on details <see cref="LeadRules.Validate"/> refuses: the application checks first.</summary>
    /// <param name="ownerUserId">The person looking after it. Null for a lead from the storefront, which nobody has picked up yet.</param>
    /// <param name="byName">Who opened it, for the history: a person's name, or "Website".</param>
    public static Lead Open(
        Guid agencyId,
        Guid customerId,
        LeadSource source,
        LeadDetails details,
        string currency,
        Guid? ownerUserId,
        string byName,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(byName);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Not a lead source.");
        }

        var tidy = details.Normalised();

        if (LeadRules.Validate(tidy) is { Count: > 0 } problems)
        {
            throw new ArgumentException($"The lead cannot be saved: {problems[0].Message}", nameof(details));
        }

        var lead = new Lead
        {
            AgencyId = agencyId,
            CustomerId = customerId,
            Source = source,
            Destination = tidy.Destination,
            TravelFrom = tidy.TravelFrom,
            TravelTo = tidy.TravelTo,
            Adults = tidy.Adults,
            Children = tidy.Children,
            BudgetMinMinor = tidy.BudgetMinMinor,
            BudgetMaxMinor = tidy.BudgetMaxMinor,
            Currency = currency.Trim().ToUpperInvariant(),
            Message = tidy.Message,
            Stage = LeadStage.New,
            OwnerUserId = ownerUserId,
        };

        lead._history.Add(LeadStageChange.Record(agencyId, lead.Id, LeadStage.New, null, ownerUserId, byName, now));

        return lead;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid CustomerId { get; private set; }

    public LeadSource Source { get; private set; }

    public string Destination { get; private set; }

    public DateOnly? TravelFrom { get; private set; }

    public DateOnly? TravelTo { get; private set; }

    public int Adults { get; private set; }

    public int Children { get; private set; }

    public Money? BudgetMinMinor { get; private set; }

    public Money? BudgetMaxMinor { get; private set; }

    /// <summary>The agency's currency when the lead came in: budgets are in it.</summary>
    public string Currency { get; private set; }

    /// <summary>What the customer wrote, or what the agent noted. May be empty.</summary>
    public string Message { get; private set; }

    public LeadStage Stage { get; private set; }

    /// <summary>Why it was lost. Set exactly while <see cref="Stage"/> is Lost.</summary>
    public string? LostReason { get; private set; }

    /// <summary>The person looking after the lead, if anyone is yet.</summary>
    public Guid? OwnerUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Every stage the lead has been at, oldest first. Only ever added to.</summary>
    public IReadOnlyList<LeadStageChange> History => _history;

    /// <summary>True until the lead is Won or Lost.</summary>
    public bool IsOpen => Stage is not (LeadStage.Won or LeadStage.Lost);

    /// <summary>Moves the lead to <paramref name="stage"/>, and writes the move to its history.</summary>
    /// <exception cref="ArgumentException">A move <see cref="LeadRules.CheckMove"/> refuses: the application checks first.</exception>
    /// <exception cref="InvalidOperationException">The lead is already at <paramref name="stage"/>.</exception>
    public void MoveTo(LeadStage stage, string? reason, Guid? byUserId, string byName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(byName);

        if (LeadRules.CheckMove(stage, reason) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message, nameof(reason));
        }

        if (stage == Stage)
        {
            throw new InvalidOperationException($"This lead is already {stage.ToString().ToLowerInvariant()}.");
        }

        // Only a lost lead keeps its reason: moving it on again means the reason no longer holds.
        var why = stage == LeadStage.Lost ? reason!.Trim() : null;

        Stage = stage;
        LostReason = why;
        _history.Add(LeadStageChange.Record(AgencyId, Id, stage, why, byUserId, byName, now));
    }

    /// <summary>
    /// A quote for this lead has gone to the customer: a new lead becomes Quoted. A lead already further
    /// along stays where it is — a second quote to a customer who is negotiating does not move them back.
    /// </summary>
    /// <returns>True when the lead moved.</returns>
    public bool MarkQuoted(Guid? byUserId, string byName, DateTimeOffset now)
    {
        if (Stage != LeadStage.New)
        {
            return false;
        }

        MoveTo(LeadStage.Quoted, null, byUserId, byName, now);
        return true;
    }
}

/// <summary>One move of a lead from one stage to another: <c>crm.lead_stage_history</c>.</summary>
/// <remarks>Append-only. The application role may insert these and never update or delete them.</remarks>
public sealed class LeadStageChange : Entity, ITenantScoped
{
    private LeadStageChange()
    {
        ByName = string.Empty;
    }

    internal static LeadStageChange Record(
        Guid agencyId,
        Guid leadId,
        LeadStage stage,
        string? reason,
        Guid? byUserId,
        string byName,
        DateTimeOffset at) =>
        new()
        {
            AgencyId = agencyId,
            LeadId = leadId,
            Stage = stage,
            Reason = reason,
            ByUserId = byUserId,
            ByName = byName.Trim(),
            At = at.ToUniversalTime(),
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid LeadId { get; private set; }

    /// <summary>The stage the lead moved to.</summary>
    public LeadStage Stage { get; private set; }

    /// <summary>Why, for a move to Lost. Null otherwise.</summary>
    public string? Reason { get; private set; }

    /// <summary>Who moved it. Null when nobody at the agency did: the storefront opened it.</summary>
    public Guid? ByUserId { get; private set; }

    /// <summary>
    /// Who moved it, as their name read at the time — so the history still reads correctly after
    /// someone changes their name or leaves the agency.
    /// </summary>
    public string ByName { get; private set; }

    public DateTimeOffset At { get; private set; }
}
