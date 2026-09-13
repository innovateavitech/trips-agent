using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>What a task or a message is about: its customer, and its lead where there is one.</summary>
public sealed record CrmSubject(Guid CustomerId, Guid? LeadId, string CustomerName);

/// <summary>
/// Builds the CRM's responses — the board's cards, a lead with everything around it, a quote, a task
/// with its label — from the database, in one place, so every screen shows the same thing the same way.
/// </summary>
/// <remarks>
/// Every read goes through the tenant filter: another agency's lead, quote or customer is simply not
/// there. Each list is read with a handful of set queries, never one query per row.
/// </remarks>
public sealed class CrmReader
{
    private readonly IAppDbContext _db;
    private readonly IStorefrontDirectory _storefront;

    public CrmReader(IAppDbContext db, IStorefrontDirectory storefront)
    {
        _db = db;
        _storefront = storefront;
    }

    public static CustomerRefResponse CustomerRef(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return new(customer.Id, customer.Name, customer.Email, customer.Phone);
    }

    /// <summary>The leads in <paramref name="leads"/> as board cards, newest first.</summary>
    public async Task<List<LeadSummaryResponse>> LeadSummariesAsync(IQueryable<Lead> leads, CancellationToken cancellationToken = default)
    {
        var rows = await leads.AsNoTracking()
            .Join(
                _db.Customers.AsNoTracking(),
                lead => lead.CustomerId,
                customer => customer.Id,
                (lead, customer) => new { Lead = lead, Customer = customer })
            .OrderByDescending(row => row.Lead.CreatedAt)
            .ThenByDescending(row => row.Lead.Id)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Lead.Id).ToList();

        var quoteCounts = await _db.Quotes.AsNoTracking()
            .Where(quote => ids.Contains(quote.LeadId))
            .GroupBy(quote => quote.LeadId)
            .Select(group => new { LeadId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.LeadId, entry => entry.Count, cancellationToken);

        // A quote's tasks carry its lead too, so the lead's next task includes theirs.
        var nextTasks = await _db.FollowUpTasks.AsNoTracking()
            .Where(task => task.CompletedAt == null && task.LeadId != null && ids.Contains(task.LeadId.Value))
            .GroupBy(task => task.LeadId)
            .Select(group => new { LeadId = group.Key, DueAt = group.Min(task => task.DueAt) })
            .ToListAsync(cancellationToken);

        var nextDue = nextTasks.ToDictionary(entry => entry.LeadId!.Value, entry => entry.DueAt);
        var owners = await NamesAsync(rows.Select(row => row.Lead.OwnerUserId), cancellationToken);

        return rows
            .Select(row => Summary(
                row.Lead,
                row.Customer,
                row.Lead.OwnerUserId is { } owner ? owners.GetValueOrDefault(owner) : null,
                nextDue.TryGetValue(row.Lead.Id, out var due) ? due : null,
                quoteCounts.GetValueOrDefault(row.Lead.Id)))
            .ToList();
    }

    /// <summary>One lead with its history, quotes, tasks and messages, or null when the agency has none with that id.</summary>
    public async Task<LeadResponse?> LeadAsync(Guid leadId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == leadId, cancellationToken);

        if (lead is null)
        {
            return null;
        }

        var summary = (await LeadSummariesAsync(_db.Leads.Where(candidate => candidate.Id == leadId), cancellationToken))[0];

        var history = (await _db.LeadStageHistory.AsNoTracking()
                .Where(change => change.LeadId == leadId)
                .OrderBy(change => change.At)
                .ThenBy(change => change.Id)
                .ToListAsync(cancellationToken))
            .Select(change => new StageChangeResponse(change.Stage.ToString(), change.At, change.ByName, change.Reason))
            .ToList();

        var quotes = await QuoteSummariesAsync(_db.Quotes.Where(quote => quote.LeadId == leadId), today, cancellationToken);
        var tasks = await TasksAsync(_db.FollowUpTasks.Where(task => task.LeadId == leadId), cancellationToken);
        var messages = await CommunicationsAsync(_db.Communications.Where(message => message.LeadId == leadId), cancellationToken);

        return new LeadResponse(
            summary.Id,
            summary.Customer,
            summary.Source,
            summary.Destination,
            summary.TravelFrom,
            summary.TravelTo,
            summary.Adults,
            summary.Children,
            summary.BudgetMaxMinor,
            summary.Currency,
            summary.Stage,
            summary.OwnerName,
            summary.CreatedAt,
            summary.NextTaskDueAt,
            summary.QuoteCount,
            lead.Message,
            lead.BudgetMinMinor?.AmountMinor,
            lead.LostReason,
            history,
            quotes,
            tasks,
            messages);
    }

    /// <summary>The quotes in <paramref name="quotes"/> as list lines, newest first.</summary>
    public static async Task<List<QuoteSummaryResponse>> QuoteSummariesAsync(
        IQueryable<Quote> quotes,
        DateOnly today,
        CancellationToken cancellationToken = default) =>
        (await quotes.AsNoTracking()
            .OrderByDescending(quote => quote.CreatedAt)
            .ThenByDescending(quote => quote.Number)
            .ToListAsync(cancellationToken))
        .Select(quote => QuoteSummary(quote, today))
        .ToList();

    /// <summary>One whole quote, or null when the agency has none with that id.</summary>
    public async Task<QuoteResponse?> QuoteAsync(Guid quoteId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var quote = await _db.Quotes.AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.Itinerary)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Id == quoteId, cancellationToken);

        if (quote is null)
        {
            return null;
        }

        var customer = await _db.Leads.AsNoTracking()
            .Where(lead => lead.Id == quote.LeadId)
            .Join(_db.Customers.AsNoTracking(), lead => lead.CustomerId, candidate => candidate.Id, (_, candidate) => candidate)
            .FirstAsync(cancellationToken);

        var publicUrl = quote.PublicToken is { } token ? await PublicUrlAsync(quote.AgencyId, token, cancellationToken) : null;

        return new QuoteResponse(
            quote.Id,
            quote.QuoteNumber,
            quote.LeadId,
            CustomerRef(customer),
            quote.Title,
            quote.StatusOn(today).ToString(),
            quote.ValidUntil,
            quote.Currency,
            Items(quote),
            Days(quote),
            quote.Notes,
            quote.TotalMinor.AmountMinor,
            publicUrl,
            quote.SentAt,
            quote.ViewedAt,
            quote.RespondedAt);
    }

    /// <summary>The customer's link to a quote, on the agency's own storefront: <c>https://{site}/q/{token}</c>.</summary>
    public async Task<string> PublicUrlAsync(Guid agencyId, string token, CancellationToken cancellationToken = default)
    {
        var site = await _storefront.SiteUrlAsync(agencyId, cancellationToken);
        return $"{site.AbsoluteUri.TrimEnd('/')}/q/{token}";
    }

    /// <summary>The tasks in <paramref name="tasks"/>, soonest due first, each labelled with what it is about.</summary>
    public async Task<List<TaskResponse>> TasksAsync(IQueryable<FollowUpTask> tasks, CancellationToken cancellationToken = default)
    {
        var rows = await tasks.AsNoTracking()
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.Id)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var customerIds = rows.Select(task => task.CustomerId).Distinct().ToList();
        var customerNames = await _db.Customers.AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name })
            .ToDictionaryAsync(customer => customer.Id, customer => customer.Name, cancellationToken);

        var leadIds = rows.Where(task => task.RelatedType == CrmRecordType.Lead).Select(task => task.RelatedId).Distinct().ToList();
        var destinations = await _db.Leads.AsNoTracking()
            .Where(lead => leadIds.Contains(lead.Id))
            .Select(lead => new { lead.Id, lead.Destination })
            .ToDictionaryAsync(lead => lead.Id, lead => lead.Destination, cancellationToken);

        var quoteIds = rows.Where(task => task.RelatedType == CrmRecordType.Quote).Select(task => task.RelatedId).Distinct().ToList();
        var quoteNumbers = await _db.Quotes.AsNoTracking()
            .Where(quote => quoteIds.Contains(quote.Id))
            .Select(quote => new { quote.Id, quote.QuoteNumber })
            .ToDictionaryAsync(quote => quote.Id, quote => quote.QuoteNumber, cancellationToken);

        var owners = await NamesAsync(rows.Select(task => task.OwnerUserId), cancellationToken);

        return rows
            .Select(task =>
            {
                var customerName = customerNames.GetValueOrDefault(task.CustomerId, string.Empty);

                // "Chiamaka Okonkwo · Dubai", "QT-0007 · Adeola Martins", or the customer's name.
                var label = task.RelatedType switch
                {
                    CrmRecordType.Lead => $"{customerName} · {destinations.GetValueOrDefault(task.RelatedId, string.Empty)}",
                    CrmRecordType.Quote => $"{quoteNumbers.GetValueOrDefault(task.RelatedId, string.Empty)} · {customerName}",
                    _ => customerName,
                };

                return new TaskResponse(
                    task.Id,
                    task.Title,
                    task.DueAt,
                    task.CompletedAt,
                    new TaskRelatedResponse(task.RelatedType.ToString(), task.RelatedId, label),
                    task.OwnerUserId is { } owner ? owners.GetValueOrDefault(owner) : null);
            })
            .ToList();
    }

    /// <summary>The messages in <paramref name="messages"/>, newest first.</summary>
    public static async Task<List<CommunicationResponse>> CommunicationsAsync(
        IQueryable<Communication> messages,
        CancellationToken cancellationToken = default) =>
        (await messages.AsNoTracking()
            .OrderByDescending(message => message.At)
            .ThenByDescending(message => message.Id)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    /// <summary>What a task or a message is about, or null when the agency has no such lead, quote or customer.</summary>
    public async Task<CrmSubject?> SubjectAsync(CrmRecordType type, Guid id, CancellationToken cancellationToken = default) =>
        type switch
        {
            CrmRecordType.Lead => await _db.Leads.AsNoTracking()
                .Where(lead => lead.Id == id)
                .Join(
                    _db.Customers.AsNoTracking(),
                    lead => lead.CustomerId,
                    customer => customer.Id,
                    (lead, customer) => new CrmSubject(customer.Id, lead.Id, customer.Name))
                .FirstOrDefaultAsync(cancellationToken),

            CrmRecordType.Quote => await _db.Quotes.AsNoTracking()
                .Where(quote => quote.Id == id)
                .Join(_db.Leads.AsNoTracking(), quote => quote.LeadId, lead => lead.Id, (_, lead) => lead)
                .Join(
                    _db.Customers.AsNoTracking(),
                    lead => lead.CustomerId,
                    customer => customer.Id,
                    (lead, customer) => new CrmSubject(customer.Id, lead.Id, customer.Name))
                .FirstOrDefaultAsync(cancellationToken),

            CrmRecordType.Customer => await _db.Customers.AsNoTracking()
                .Where(customer => customer.Id == id)
                .Select(customer => new CrmSubject(customer.Id, null, customer.Name))
                .FirstOrDefaultAsync(cancellationToken),

            _ => null,
        };

    public static CommunicationResponse ToResponse(Communication message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new(
            message.Id,
            message.Channel.ToString(),
            message.Direction.ToString(),
            message.Summary,
            message.At,
            message.ByName,
            new RelatedRecord(message.RelatedType.ToString(), message.RelatedId));
    }

    public static QuoteSummaryResponse QuoteSummary(Quote quote, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return new(
            quote.Id,
            quote.QuoteNumber,
            quote.Title,
            quote.StatusOn(today).ToString(),
            quote.TotalMinor.AmountMinor,
            quote.Currency,
            quote.ValidUntil,
            quote.SentAt);
    }

    /// <summary>A quote's items in the agent's order. Needs them loaded.</summary>
    public static IReadOnlyList<QuoteItemResponse> Items(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return quote.Items
            .OrderBy(item => item.Position)
            .Select(item => new QuoteItemResponse(item.Description, item.Quantity, item.UnitPriceMinor.AmountMinor, item.ProductId))
            .ToList();
    }

    /// <summary>A quote's days, day 1 first. Needs them loaded.</summary>
    public static IReadOnlyList<QuoteDayResponse> Days(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return quote.Itinerary
            .OrderBy(day => day.DayNumber)
            .Select(day => new QuoteDayResponse(day.DayNumber, day.Title, day.Description))
            .ToList();
    }

    private static LeadSummaryResponse Summary(
        Lead lead,
        Customer customer,
        string? ownerName,
        DateTimeOffset? nextTaskDueAt,
        int quoteCount) =>
        new(
            lead.Id,
            CustomerRef(customer),
            lead.Source.ToString(),
            lead.Destination,
            lead.TravelFrom,
            lead.TravelTo,
            lead.Adults,
            lead.Children,
            lead.BudgetMaxMinor?.AmountMinor,
            lead.Currency,
            lead.Stage.ToString(),
            ownerName,
            lead.CreatedAt,
            nextTaskDueAt,
            quoteCount);

    /// <summary>The names of the people with these ids, where they can be read.</summary>
    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var users = await _db.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        var names = new Dictionary<Guid, string>();

        foreach (var user in users)
        {
            if (CrmContext.PersonName(user.FirstName, user.LastName) is { } name)
            {
                names[user.Id] = name;
            }
        }

        return names;
    }
}
