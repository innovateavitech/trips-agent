using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// Follow-up tasks and the communication timeline (#62): what the agency still has to do, and what
/// has already been said.
/// </summary>
/// <remarks>
/// <para>
/// Both hang off a lead, a quote or a customer. Whichever is named, the customer is worked out from
/// it and stored alongside — so a customer's record shows every task and message about them, including
/// those raised against one of their leads or quotes, without a join through three tables.
/// </para>
/// <para>
/// <b>SMS and WhatsApp are logged, not sent.</b> A communication row is a note of what happened
/// elsewhere; nothing here contacts anyone.
/// </para>
/// </remarks>
public sealed class FollowUpService
{
    private readonly IAppDbContext _db;
    private readonly CrmContext _crm;
    private readonly CrmReader _reader;

    public FollowUpService(IAppDbContext db, CrmContext crm, CrmReader reader)
    {
        _db = db;
        _crm = crm;
        _reader = reader;
    }

    /// <summary>The agency's tasks, soonest due first.</summary>
    /// <param name="openOnly">True for only the ones still to do.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<List<TaskResponse>> ListTasksAsync(bool openOnly, CancellationToken cancellationToken = default) =>
        _reader.TasksAsync(
            openOnly ? _db.FollowUpTasks.Where(task => task.CompletedAt == null) : _db.FollowUpTasks,
            cancellationToken);

    /// <summary>Adds a task against a lead, a quote or a customer. Its owner is whoever added it.</summary>
    public async Task<CrmResult<TaskResponse>> AddTaskAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);
        var problems = FollowUpTask.Check(request.Title).ToList();

        if (EnumNames.Parse<CrmRecordType>(request.Related?.Type) is not { } relatedType)
        {
            problems.Add(new CrmProblem("related.type", "Choose Lead, Customer or Quote."));
            return new CrmResult<TaskResponse>.Invalid("This task cannot be saved yet.", problems);
        }

        // The customer the task is really about, found from whatever it was raised against. Another
        // agency's lead is not there to find.
        var subject = await _reader.SubjectAsync(relatedType, request.Related!.Id, cancellationToken);

        if (subject is null)
        {
            return new CrmResult<TaskResponse>.NotFound(
                $"We could not find that {relatedType.ToString().ToLowerInvariant()}.");
        }

        if (problems.Count > 0)
        {
            return new CrmResult<TaskResponse>.Invalid("This task cannot be saved yet.", problems);
        }

        var task = FollowUpTask.Create(
            actor.AgencyId,
            request.Title,
            request.DueAt,
            relatedType,
            request.Related.Id,
            subject.CustomerId,
            subject.LeadId,
            actor.UserId);

        _db.FollowUpTasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<TaskResponse>.Done(
            (await _reader.TasksAsync(_db.FollowUpTasks.Where(row => row.Id == task.Id), cancellationToken))[0]);
    }

    /// <summary>Marks a task done. Completing one that is already done is refused (409).</summary>
    public async Task<CrmResult<TaskResponse>> CompleteTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var actor = await _crm.ActorAsync(cancellationToken);
        var task = await _db.FollowUpTasks.FirstOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);

        if (task is null)
        {
            return new CrmResult<TaskResponse>.NotFound("We could not find that task.");
        }

        if (!task.IsOpen)
        {
            return new CrmResult<TaskResponse>.Refused("That task is already done.", "Nothing has changed.");
        }

        task.Complete(actor.Now);
        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<TaskResponse>.Done(
            (await _reader.TasksAsync(_db.FollowUpTasks.Where(row => row.Id == taskId), cancellationToken))[0]);
    }

    /// <summary>Puts a message on the timeline. Nothing is sent — this records what already happened.</summary>
    public async Task<CrmResult<CommunicationResponse>> LogAsync(
        CommunicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);
        var problems = Communication.Check(request.Summary).ToList();

        var channel = EnumNames.Parse<CommunicationChannel>(request.Channel);
        var direction = EnumNames.Parse<CommunicationDirection>(request.Direction);
        var relatedType = EnumNames.Parse<CrmRecordType>(request.Related?.Type);

        if (channel is null)
        {
            problems.Add(new CrmProblem("channel", "Choose Email, Sms, Whatsapp, Call or Note."));
        }

        if (direction is null)
        {
            problems.Add(new CrmProblem("direction", "Choose Inbound or Outbound."));
        }

        if (relatedType is null)
        {
            problems.Add(new CrmProblem("related.type", "Choose Lead, Customer or Quote."));
            return new CrmResult<CommunicationResponse>.Invalid("This message cannot be saved.", problems);
        }

        var subject = await _reader.SubjectAsync(relatedType.Value, request.Related!.Id, cancellationToken);

        if (subject is null)
        {
            return new CrmResult<CommunicationResponse>.NotFound(
                $"We could not find that {relatedType.Value.ToString().ToLowerInvariant()}.");
        }

        if (problems.Count > 0)
        {
            return new CrmResult<CommunicationResponse>.Invalid("This message cannot be saved.", problems);
        }

        // Inbound came from the customer; outbound came from whoever is signed in.
        var inbound = direction!.Value == CommunicationDirection.Inbound;

        var message = Communication.Log(
            actor.AgencyId,
            channel!.Value,
            direction.Value,
            request.Summary,
            relatedType.Value,
            request.Related.Id,
            subject.CustomerId,
            subject.LeadId,
            inbound ? null : actor.UserId,
            inbound ? subject.CustomerName : actor.Name,
            actor.Now);

        _db.Communications.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<CommunicationResponse>.Done(CrmReader.ToResponse(message));
    }
}
