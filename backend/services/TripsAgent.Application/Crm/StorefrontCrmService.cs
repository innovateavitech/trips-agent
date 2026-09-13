using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Commerce;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>
/// The CRM's anonymous side: the storefront's trip-request form, and the quote page a customer opens
/// from their email (#62, F4).
/// </summary>
/// <remarks>
/// <para>
/// <b>No tenant arrives with the request.</b> A traveller on an agency's site carries no token, so the
/// agency is found from the host name they used — by <see cref="StorefrontTenant"/>, the one place every
/// public storefront route resolves a host, which enters <c>IPlatformScope</c> with a reason to do it and
/// then moves the request inside that agency's tenant filter. A host that belongs to nobody is not found,
/// and every route here says the same thing to it as to a host that exists with no such quote: an
/// anonymous caller learns nothing about which agencies exist.
/// </para>
/// <para>
/// <b>A suspended agency's shop is shut</b> (decision 14, issue 171). Taking a trip request and accepting
/// a quote are new business, so both need <c>CanServeStorefront</c>; reading a quote the customer already
/// holds is a traveller coming back to something of theirs, which only a terminated agency refuses. This
/// service used to resolve the host its own way and so missed the rule entirely.
/// </para>
/// <para>
/// Nothing here may mention Trips (CLAUDE.md rule 4), and nothing here returns an agent-facing field:
/// a traveller sees their own quote's items and days, never the lead behind it, its owner, or what
/// anybody paid.
/// </para>
/// </remarks>
public sealed class StorefrontCrmService
{
    private readonly IAppDbContext _db;
    private readonly CrmContext _crm;
    private readonly CustomerDirectory _customers;
    private readonly StorefrontTenant _storefront;
    private readonly IUniqueViolationDetector _uniqueViolations;

    public StorefrontCrmService(
        IAppDbContext db,
        CrmContext crm,
        CustomerDirectory customers,
        StorefrontTenant storefront,
        IUniqueViolationDetector uniqueViolations)
    {
        _db = db;
        _crm = crm;
        _customers = customers;
        _storefront = storefront;
        _uniqueViolations = uniqueViolations;
    }

    /// <summary>
    /// Takes a trip request from an agency's storefront: the customer is created or updated, and a New
    /// lead is opened with source <see cref="LeadSource.TripRequestWidget"/>.
    /// </summary>
    /// <param name="host">The host name the traveller's browser used. Lower-case, without a port.</param>
    /// <param name="submission">What they filled in.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    /// <returns>Done with nothing to show — the form says thank you, and the agent picks the lead up.</returns>
    public async Task<CrmResult<Guid>> SubmitTripRequestAsync(
        string? host,
        TripRequestSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // A lead is new business, so a suspended or terminated agency's widget takes none.
        if (await _storefront.EnterAsync(host, StorefrontVisit.Shopping, cancellationToken) is null)
        {
            return new CrmResult<Guid>.NotFound("We could not find that site.");
        }

        var details = Details(submission);
        var problems = ContactDetails
            .Check(submission.Name, submission.Email, submission.Phone, string.Empty)
            .Concat(LeadRules.Validate(details))
            .ToList();

        if (problems.Count > 0)
        {
            return new CrmResult<Guid>.Invalid("We could not send that request.", problems);
        }

        var agency = await _crm.AgencyAsync(cancellationToken);

        var lead = await CrmSaves.RetryOnceOnUniqueViolationAsync(
            _db,
            _uniqueViolations,
            async () =>
            {
                var customer = await _customers.FindOrAddAsync(
                    agency.Id, submission.Name, submission.Email, submission.Phone, agency.Now, cancellationToken);

                // Nobody owns it yet: it goes to the inbox, and an agent picks it up.
                var opened = Lead.Open(
                    agency.Id,
                    customer.Id,
                    LeadSource.TripRequestWidget,
                    details,
                    agency.Currency,
                    ownerUserId: null,
                    CrmContext.WebsiteName,
                    agency.Now);

                _db.Leads.Add(opened);
                return opened;
            },
            cancellationToken);

        return new CrmResult<Guid>.Done(lead.Id);
    }

    /// <summary>
    /// The quote behind a public link, as its customer sees it. The first view is recorded — after
    /// that, opening it again changes nothing.
    /// </summary>
    /// <param name="host">The host name the traveller's browser used.</param>
    /// <param name="token">The secret in the link.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<CrmResult<PublicQuoteResponse>> ViewQuoteAsync(
        string? host,
        string? token,
        CancellationToken cancellationToken = default)
    {
        // Reading a quote they were sent is a customer coming back to something of theirs, so it
        // survives a suspension and stops only when the agency is terminated.
        if (await FindQuoteAsync(host, token, StorefrontVisit.ExistingQuote, cancellationToken) is not { } found)
        {
            return new CrmResult<PublicQuoteResponse>.NotFound(NoSuchQuote);
        }

        // Only the first view counts, so a customer who reads it four times is not four views.
        if (found.Quote.RecordView(found.Agency.Now))
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new CrmResult<PublicQuoteResponse>.Done(
            Public(found.Quote, found.Customer, found.Agency.Today, found.CanSell));
    }

    /// <summary>The customer accepts their quote.</summary>
    public Task<CrmResult<PublicQuoteResponse>> AcceptQuoteAsync(
        string? host,
        string? token,
        CancellationToken cancellationToken = default) =>
        AnswerAsync(host, token, accepted: true, reason: null, cancellationToken);

    /// <summary>The customer declines their quote, with their reason if they gave one.</summary>
    public Task<CrmResult<PublicQuoteResponse>> DeclineQuoteAsync(
        string? host,
        string? token,
        string? reason,
        CancellationToken cancellationToken = default) =>
        AnswerAsync(host, token, accepted: false, reason, cancellationToken);

    /// <summary>What every miss on a quote link says, whoever asked and whatever was wrong.</summary>
    private const string NoSuchQuote = "We could not find that quote.";

    /// <summary>
    /// Accepting or declining: the quote answers, the lead moves, and the customer's own words go on
    /// the timeline, all in one save.
    /// </summary>
    private async Task<CrmResult<PublicQuoteResponse>> AnswerAsync(
        string? host,
        string? token,
        bool accepted,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (reason is not null && reason.Trim().Length > CrmLimits.MaxReasonLength)
        {
            return new CrmResult<PublicQuoteResponse>.Invalid(
                "We could not send that.",
                [new CrmProblem("reason", "That message is too long.")]);
        }

        // Answering a quote is doing business with the shop, so a suspended agency's is shut: the
        // customer gets the same "no such quote" as anybody else, and the view above says so first
        // by answering that the quote cannot be responded to.
        if (await FindQuoteAsync(host, token, StorefrontVisit.Shopping, cancellationToken) is not { } found)
        {
            return new CrmResult<PublicQuoteResponse>.NotFound(NoSuchQuote);
        }

        var (agency, quote, customer, _) = found;

        if (quote.WhyNotAnswerable(agency.Today) is { } why)
        {
            return new CrmResult<PublicQuoteResponse>.Refused("This quote cannot be answered.", why);
        }

        var lead = await _db.Leads.FirstAsync(candidate => candidate.Id == quote.LeadId, cancellationToken);
        var tracked = await _db.Customers.FirstAsync(candidate => candidate.Id == customer.Id, cancellationToken);

        if (accepted)
        {
            quote.Accept(agency.Today, agency.Now);
        }
        else
        {
            quote.Decline(agency.Today, agency.Now);
        }

        // Won or Lost is the agent's call: a declined quote is one quote, and the customer may still
        // take a different one. What the pipeline learns is that the conversation is live again.
        if (lead.Stage is LeadStage.New or LeadStage.Quoted)
        {
            lead.MoveTo(LeadStage.Negotiating, null, null, CrmContext.WebsiteName, agency.Now);
        }

        tracked.RecordActivity(agency.Now);

        var said = accepted
            ? $"Accepted {quote.QuoteNumber} on the website."
            : Declined(quote.QuoteNumber, reason);

        _db.Communications.Add(Communication.Log(
            agency.Id,
            CommunicationChannel.Note,
            CommunicationDirection.Inbound,
            said,
            CrmRecordType.Quote,
            quote.Id,
            tracked.Id,
            lead.Id,
            byUserId: null,
            tracked.Name,
            agency.Now));

        await _db.SaveChangesAsync(cancellationToken);

        // Reached only through a shop that may sell, so the answer it returns says so.
        return new CrmResult<PublicQuoteResponse>.Done(Public(quote, tracked, agency.Today, canSell: true));
    }

    private static string Declined(string quoteNumber, string? reason)
    {
        var said = reason?.Trim();

        return said is { Length: > 0 }
            ? $"Declined {quoteNumber} on the website: {said}"
            : $"Declined {quoteNumber} on the website.";
    }

    /// <summary>
    /// The quote a link points at, inside its agency's tenant — or nothing, for any reason at all:
    /// an unknown host, a shop that may not serve this kind of visit, a token the wrong shape, a
    /// token nobody has, or a quote not yet sent.
    /// </summary>
    private async Task<PublicQuoteRecord?> FindQuoteAsync(
        string? host,
        string? token,
        StorefrontVisit visit,
        CancellationToken cancellationToken)
    {
        // Checked before the database is asked: a token of the wrong shape is somebody probing.
        if (!QuoteLinkTokens.LooksValid(token))
        {
            return null;
        }

        var shop = await _storefront.EnterAsync(host, visit, cancellationToken);

        if (shop is null)
        {
            return null;
        }

        var agency = await _crm.AgencyAsync(cancellationToken);

        var quote = await _db.Quotes
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.Itinerary)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.PublicToken == token, cancellationToken);

        if (quote is null)
        {
            return null;
        }

        var customer = await _db.Leads.AsNoTracking()
            .Where(lead => lead.Id == quote.LeadId)
            .Join(_db.Customers.AsNoTracking(), lead => lead.CustomerId, candidate => candidate.Id, (_, candidate) => candidate)
            .FirstAsync(cancellationToken);

        return new PublicQuoteRecord(agency, quote, customer, shop.CanSell);
    }

    private static PublicQuoteResponse Public(Quote quote, Customer customer, DateOnly today, bool canSell) =>
        new(
            quote.QuoteNumber,
            quote.Title,
            quote.StatusOn(today).ToString(),
            quote.ValidUntil,
            quote.Currency,
            CrmReader.Items(quote),
            CrmReader.Days(quote),
            quote.Notes,
            quote.TotalMinor.AmountMinor,
            customer.Name,
            quote.SentAt!.Value,
            quote.RespondedAt,

            // A shut shop cannot be answered, so the page is never offered a button the next
            // request would refuse (decision 14).
            quote.WhyNotAnswerable(today) is null && canSell);

    /// <summary>A quote found behind a public link, with the agency it belongs to and who it is for.</summary>
    /// <param name="CanSell">Whether that agency may take new business today.</param>
    private sealed record PublicQuoteRecord(CrmAgency Agency, Quote Quote, Customer Customer, bool CanSell);

    private static LeadDetails Details(TripRequestSubmission submission) =>
        new LeadDetails(
            submission.Destination,
            submission.TravelFrom,
            submission.TravelTo,
            submission.Adults,
            submission.Children,
            submission.BudgetMinMinor is { } min ? new Money(min) : null,
            submission.BudgetMaxMinor is { } max ? new Money(max) : null,
            submission.Message).Normalised();
}
