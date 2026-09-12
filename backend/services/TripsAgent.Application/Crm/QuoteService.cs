using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// The quote builder (#62): a draft against a lead, saved as often as the agent likes, and sent once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sending is the line.</b> A draft is the agency's working copy; a sent quote is what the customer
/// has in their inbox, and it never changes again — a save against it is refused (409), and new terms
/// mean a new quote. Sending gives it its public link, emails the customer, and moves a New lead to
/// Quoted.
/// </para>
/// <para>
/// Quote numbers are the agency's own sequence, QT-0001 upward, and the database has a unique index
/// per agency: two agents numbering at the same moment meet it, and the second retries with the next
/// number rather than taking the first one's.
/// </para>
/// </remarks>
public sealed class QuoteService
{
    /// <summary>How many times a number collision is retried before giving up. Each attempt re-reads the highest.</summary>
    private const int NumberAttempts = 5;

    private readonly IAppDbContext _db;
    private readonly CrmContext _crm;
    private readonly CrmReader _reader;
    private readonly IQuoteEmails _emails;
    private readonly IUniqueViolationDetector _uniqueViolations;

    public QuoteService(
        IAppDbContext db,
        CrmContext crm,
        CrmReader reader,
        IQuoteEmails emails,
        IUniqueViolationDetector uniqueViolations)
    {
        _db = db;
        _crm = crm;
        _reader = reader;
        _emails = emails;
        _uniqueViolations = uniqueViolations;
    }

    /// <summary>One quote, or null when the agency has no such quote.</summary>
    public async Task<QuoteResponse?> GetAsync(Guid quoteId, CancellationToken cancellationToken = default)
    {
        var agency = await _crm.AgencyAsync(cancellationToken);
        return await _reader.QuoteAsync(quoteId, agency.Today, cancellationToken);
    }

    /// <summary>Starts a draft quote for a lead.</summary>
    public async Task<CrmResult<QuoteResponse>> CreateAsync(
        Guid leadId,
        QuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);
        var content = Content(request);

        if (QuoteRules.Validate(content, actor.Today) is { Count: > 0 } problems)
        {
            return new CrmResult<QuoteResponse>.Invalid("This quote cannot be saved yet.", problems);
        }

        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == leadId, cancellationToken);

        if (lead is null)
        {
            return new CrmResult<QuoteResponse>.NotFound("We could not find that lead.");
        }

        for (var attempt = 1; ; attempt++)
        {
            var quote = Quote.Draft(
                actor.AgencyId, lead.Id, await NextNumberAsync(cancellationToken), lead.Currency, content, actor.UserId);

            _db.Quotes.Add(quote);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new CrmResult<QuoteResponse>.Done((await _reader.QuoteAsync(quote.Id, actor.Today, cancellationToken))!);
            }
            catch (DbUpdateException ex) when (attempt < NumberAttempts && _uniqueViolations.IsUniqueViolation(ex))
            {
                // Another agent took this number a moment ago. Drop the draft and number again.
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Replaces everything on a draft. A sent quote is refused: the customer has it as it was sent.</summary>
    public async Task<CrmResult<QuoteResponse>> SaveAsync(
        Guid quoteId,
        QuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await _crm.ActorAsync(cancellationToken);
        var content = Content(request);

        if (QuoteRules.Validate(content, actor.Today) is { Count: > 0 } problems)
        {
            return new CrmResult<QuoteResponse>.Invalid("This quote cannot be saved yet.", problems);
        }

        var quote = await LoadAsync(quoteId, cancellationToken);

        if (quote is null)
        {
            return new CrmResult<QuoteResponse>.NotFound("We could not find that quote.");
        }

        if (quote.Status != QuoteStatus.Draft)
        {
            return new CrmResult<QuoteResponse>.Refused(
                "A sent quote cannot be changed.",
                "The customer has it as it was sent. Make a new quote for new terms.");
        }

        quote.Revise(content);
        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<QuoteResponse>.Done((await _reader.QuoteAsync(quote.Id, actor.Today, cancellationToken))!);
    }

    /// <summary>
    /// Sends the quote to its customer: it gets its public link, the customer gets an email in the
    /// agency's branding, and a New lead becomes Quoted.
    /// </summary>
    /// <remarks>
    /// The link, the email and the lead's move all commit together — a customer is never sent a link
    /// to a quote the database does not think was sent.
    /// </remarks>
    public async Task<CrmResult<QuoteResponse>> SendAsync(Guid quoteId, CancellationToken cancellationToken = default)
    {
        var actor = await _crm.ActorAsync(cancellationToken);
        var quote = await LoadAsync(quoteId, cancellationToken);

        if (quote is null)
        {
            return new CrmResult<QuoteResponse>.NotFound("We could not find that quote.");
        }

        if (QuoteRules.WhyNotSendable(quote, actor.Today) is { } why)
        {
            return new CrmResult<QuoteResponse>.Refused("This quote cannot be sent yet.", why);
        }

        var lead = await _db.Leads.FirstAsync(candidate => candidate.Id == quote.LeadId, cancellationToken);
        var customer = await _db.Customers.FirstAsync(candidate => candidate.Id == lead.CustomerId, cancellationToken);

        quote.Send(QuoteLinkTokens.New(), actor.Today, actor.Now);
        lead.MarkQuoted(actor.UserId, actor.Name, actor.Now);
        customer.RecordActivity(actor.Now);

        var publicUrl = await _reader.PublicUrlAsync(actor.AgencyId, quote.PublicToken!, cancellationToken);
        await _emails.QueueQuoteSentAsync(quote, customer, publicUrl, actor, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return new CrmResult<QuoteResponse>.Done((await _reader.QuoteAsync(quote.Id, actor.Today, cancellationToken))!);
    }

    private static QuoteContent Content(QuoteRequest request) =>
        new QuoteContent(
            request.Title,
            request.ValidUntil,
            (request.Items ?? [])
                .Select(item => new QuoteItemContent(item.Description, item.Quantity, new Money(item.UnitPriceMinor), item.ProductId))
                .ToList(),
            (request.Itinerary ?? [])
                .Select(day => new QuoteDayContent(day.DayNumber, day.Title, day.Description))
                .ToList(),
            request.Notes).Normalised();

    private Task<Quote?> LoadAsync(Guid quoteId, CancellationToken cancellationToken) =>
        _db.Quotes
            .Include(quote => quote.Items)
            .Include(quote => quote.Itinerary)
            .AsSplitQuery()
            .FirstOrDefaultAsync(quote => quote.Id == quoteId, cancellationToken);

    /// <summary>The agency's next quote number: one past its highest. Its own sequence, from QT-0001.</summary>
    private async Task<int> NextNumberAsync(CancellationToken cancellationToken)
    {
        var highest = await _db.Quotes.AsNoTracking().MaxAsync(quote => (int?)quote.Number, cancellationToken);
        return (highest ?? 0) + 1;
    }
}
