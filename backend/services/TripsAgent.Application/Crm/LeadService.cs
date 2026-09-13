using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// The pipeline: the agency's leads, the one an agent keys in, and moving one along (#62).
/// </summary>
/// <remarks>
/// <para>
/// A lead always has a customer, and the customer is never keyed in first (FRD §2.8 RS-1):
/// <see cref="CustomerDirectory"/> finds the one these contact details belong to, or starts one, and
/// the lead is opened against it in the same save.
/// </para>
/// <para>
/// Every read and write goes through the tenant filter, so a lead id from another agency is simply
/// not found — which is what the caller is told, in the same words as an id that never existed.
/// </para>
/// </remarks>
public sealed class LeadService
{
    private readonly IAppDbContext _db;
    private readonly CrmContext _crm;
    private readonly CrmReader _reader;
    private readonly CustomerDirectory _customers;
    private readonly IUniqueViolationDetector _uniqueViolations;

    public LeadService(
        IAppDbContext db,
        CrmContext crm,
        CrmReader reader,
        CustomerDirectory customers,
        IUniqueViolationDetector uniqueViolations)
    {
        _db = db;
        _crm = crm;
        _reader = reader;
        _customers = customers;
        _uniqueViolations = uniqueViolations;
    }

    /// <summary>Every lead the agency has, newest first.</summary>
    public Task<List<LeadSummaryResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        _reader.LeadSummariesAsync(_db.Leads, cancellationToken);

    /// <summary>One lead with its history, quotes, tasks and messages, or null when the agency has no such lead.</summary>
    public async Task<LeadResponse?> GetAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        var agency = await _crm.AgencyAsync(cancellationToken);
        return await _reader.LeadAsync(leadId, agency.Today, cancellationToken);
    }

    /// <summary>
    /// Opens a lead the agent keyed in — a call, a walk-in, a message. Its source is
    /// <see cref="LeadSource.Manual"/> and its owner is whoever keyed it in.
    /// </summary>
    public async Task<CrmResult<LeadResponse>> CreateAsync(LeadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);

        var details = Details(request);
        var problems = ContactDetails
            .Check(request.Customer?.Name, request.Customer?.Email, request.Customer?.Phone, "customer.")
            .Concat(LeadRules.Validate(details))
            .ToList();

        if (problems.Count > 0)
        {
            return new CrmResult<LeadResponse>.Invalid("This lead cannot be saved yet.", problems);
        }

        var contact = request.Customer!;

        var lead = await CrmSaves.RetryOnceOnUniqueViolationAsync(
            _db,
            _uniqueViolations,
            async () =>
            {
                var customer = await _customers.FindOrAddAsync(
                    actor.AgencyId, contact.Name, contact.Email, contact.Phone, actor.Now, cancellationToken);

                var opened = Lead.Open(
                    actor.AgencyId,
                    customer.Id,
                    LeadSource.Manual,
                    details,
                    actor.Agency.Currency,
                    actor.UserId,
                    actor.Name,
                    actor.Now);

                _db.Leads.Add(opened);
                return opened;
            },
            cancellationToken);

        return new CrmResult<LeadResponse>.Done((await _reader.LeadAsync(lead.Id, actor.Today, cancellationToken))!);
    }

    /// <summary>Moves a lead along the pipeline, writing the move to its history.</summary>
    /// <remarks>
    /// Any stage can follow any other — agents fix mistakes, and lost customers come back — with two
    /// exceptions: a lead is not moved to where it already is (409), and Lost needs a reason (422).
    /// </remarks>
    public async Task<CrmResult<LeadResponse>> MoveAsync(
        Guid leadId,
        MoveLeadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);

        if (EnumNames.Parse<LeadStage>(request.Stage) is not { } stage)
        {
            return new CrmResult<LeadResponse>.Invalid(
                "This lead cannot be moved.",
                [new CrmProblem("stage", "Choose New, Quoted, Negotiating, Won or Lost.")]);
        }

        if (LeadRules.CheckMove(stage, request.Reason) is { Count: > 0 } problems)
        {
            return new CrmResult<LeadResponse>.Invalid("This lead cannot be moved.", problems);
        }

        var lead = await _db.Leads.FirstOrDefaultAsync(candidate => candidate.Id == leadId, cancellationToken);

        if (lead is null)
        {
            return new CrmResult<LeadResponse>.NotFound("We could not find that lead.");
        }

        if (lead.Stage == stage)
        {
            return new CrmResult<LeadResponse>.Refused(
                "This lead has not moved.",
                $"It is already {stage.ToString().ToLowerInvariant()}.");
        }

        lead.MoveTo(stage, request.Reason, actor.UserId, actor.Name, actor.Now);
        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<LeadResponse>.Done((await _reader.LeadAsync(lead.Id, actor.Today, cancellationToken))!);
    }

    private static LeadDetails Details(LeadRequest request) =>
        new LeadDetails(
            request.Destination,
            request.TravelFrom,
            request.TravelTo,
            request.Adults,
            request.Children,
            request.BudgetMinMinor is { } min ? new Money(min) : null,
            request.BudgetMaxMinor is { } max ? new Money(max) : null,
            request.Message).Normalised();
}
