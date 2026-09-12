using System.Globalization;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Crm;

/// <summary>
/// A priced proposal the agency writes for a lead, and sends to the customer as a link on the
/// agency's own storefront, where the customer accepts or declines it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sent quote never changes.</b> The customer has it as it was sent — the items, the prices, the
/// days — so <see cref="Revise"/> works on a draft only. New terms are a new quote.
/// </para>
/// <para>
/// <b>Status</b> runs Draft → Sent → Viewed → Accepted or Declined. Expired is not stored: a sent quote
/// whose last valid day has passed <i>reads</i> as expired (<see cref="StatusOn"/>), so it can never
/// be accepted late, and there is no nightly job to forget to run.
/// </para>
/// <para>
/// <b>A quote is not a tax document.</b> Its number runs in sequence per agency, but it is not gapless
/// and does not take the invoice numbering lock: drafts are abandoned constantly, and each would
/// otherwise hold a lock on a shared counter.
/// </para>
/// </remarks>
public sealed class Quote : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>What a quote number starts with: <c>QT-0007</c>.</summary>
    public const string NumberPrefix = "QT-";

    private readonly List<QuoteItem> _items = [];
    private readonly List<QuoteItineraryDay> _itinerary = [];

    private Quote()
    {
        QuoteNumber = string.Empty;
        Title = string.Empty;
        Currency = string.Empty;
        Notes = string.Empty;
    }

    /// <summary>A draft for <paramref name="leadId"/>. Throws on content <see cref="QuoteRules.CheckShape"/> refuses.</summary>
    /// <param name="number">The agency's next quote number. Unique per agency; the database says so too.</param>
    public static Quote Draft(
        Guid agencyId,
        Guid leadId,
        int number,
        string currency,
        QuoteContent content,
        Guid? createdByUserId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(leadId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(content);

        var quote = new Quote
        {
            AgencyId = agencyId,
            LeadId = leadId,
            Number = number,
            QuoteNumber = FormatNumber(number),
            Currency = currency.Trim().ToUpperInvariant(),
            Status = QuoteStatus.Draft,
            CreatedByUserId = createdByUserId,
        };

        quote.Apply(content.Normalised());

        return quote;
    }

    /// <summary>QT-0001 to QT-9999, then QT-10000 and on: the number never wraps.</summary>
    public static string FormatNumber(int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{NumberPrefix}{number:D4}");

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid LeadId { get; private set; }

    /// <summary>The agency's sequence number for this quote. <see cref="QuoteNumber"/> is how it is shown.</summary>
    public int Number { get; private set; }

    public string QuoteNumber { get; private set; }

    public string Title { get; private set; }

    /// <summary>The stored status. Read <see cref="StatusOn"/> for what the quote is today.</summary>
    public QuoteStatus Status { get; private set; }

    /// <summary>The last day the customer can accept it, inclusive, in the agency's time zone.</summary>
    public DateOnly ValidUntil { get; private set; }

    public string Currency { get; private set; }

    public string Notes { get; private set; }

    /// <summary>Quantity times unit price over every item, worked out on each save.</summary>
    public Money TotalMinor { get; private set; }

    /// <summary>
    /// The secret in the customer's link, set when the quote is sent. Anyone holding the link can view
    /// and answer the quote, so it is 256 random bits — never an id, never guessable.
    /// </summary>
    public string? PublicToken { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>The first time the customer opened the link.</summary>
    public DateTimeOffset? ViewedAt { get; private set; }

    /// <summary>When the customer accepted or declined it.</summary>
    public DateTimeOffset? RespondedAt { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    /// <summary>
    /// Bumped by every change, and checked by the database on every save.
    /// </summary>
    /// <remarks>
    /// A draft saved in one tab while it is sent from another must not win: the customer would hold a
    /// quote that changed after it went out. With this, whichever save commits second is refused.
    /// </remarks>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The priced lines, in the agent's order.</summary>
    public IReadOnlyList<QuoteItem> Items => _items;

    /// <summary>The proposed days, day 1 first.</summary>
    public IReadOnlyList<QuoteItineraryDay> Itinerary => _itinerary;

    /// <summary>What the quote is on <paramref name="today"/>: a sent quote past its last day reads as Expired.</summary>
    public QuoteStatus StatusOn(DateOnly today) =>
        Status is QuoteStatus.Sent or QuoteStatus.Viewed && ValidUntil < today ? QuoteStatus.Expired : Status;

    /// <summary>Replaces everything on a draft: title, date, items, days and notes.</summary>
    /// <exception cref="InvalidOperationException">The quote has been sent.</exception>
    public void Revise(QuoteContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (Status != QuoteStatus.Draft)
        {
            throw new InvalidOperationException("A sent quote cannot be changed.");
        }

        Apply(content.Normalised());
        Version++;
    }

    /// <summary>Sends the draft: from now on it has a link, and it never changes.</summary>
    /// <exception cref="InvalidOperationException">Why it cannot be sent — see <see cref="QuoteRules.WhyNotSendable"/>.</exception>
    public void Send(string publicToken, DateOnly today, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicToken);

        if (QuoteRules.WhyNotSendable(this, today) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        Status = QuoteStatus.Sent;
        PublicToken = publicToken;
        SentAt = now.ToUniversalTime();
        Version++;
    }

    /// <summary>Records that the customer opened the link. Only the first time counts.</summary>
    /// <returns>True when this was the first view.</returns>
    public bool RecordView(DateTimeOffset now)
    {
        if (PublicToken is null || ViewedAt is not null)
        {
            return false;
        }

        ViewedAt = now.ToUniversalTime();

        if (Status == QuoteStatus.Sent)
        {
            Status = QuoteStatus.Viewed;
        }

        Version++;
        return true;
    }

    /// <summary>Why the customer cannot accept or decline the quote on <paramref name="today"/>, or null when they can.</summary>
    public string? WhyNotAnswerable(DateOnly today) => StatusOn(today) switch
    {
        QuoteStatus.Sent or QuoteStatus.Viewed => null,
        QuoteStatus.Accepted => "This quote has already been accepted.",
        QuoteStatus.Declined => "This quote has already been declined.",
        QuoteStatus.Expired => string.Create(
            CultureInfo.InvariantCulture,
            $"This quote could be accepted until {ValidUntil:d MMMM yyyy}. Ask for a new one."),
        _ => "This quote has not been sent.",
    };

    /// <summary>The customer accepts the quote.</summary>
    /// <exception cref="InvalidOperationException">Why it cannot be accepted — see <see cref="WhyNotAnswerable"/>.</exception>
    public void Accept(DateOnly today, DateTimeOffset now) => Answer(QuoteStatus.Accepted, today, now);

    /// <summary>The customer declines the quote.</summary>
    /// <exception cref="InvalidOperationException">Why it cannot be declined — see <see cref="WhyNotAnswerable"/>.</exception>
    public void Decline(DateOnly today, DateTimeOffset now) => Answer(QuoteStatus.Declined, today, now);

    private void Answer(QuoteStatus answer, DateOnly today, DateTimeOffset now)
    {
        if (WhyNotAnswerable(today) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var utc = now.ToUniversalTime();

        // Answering is proof of having looked, even if the view itself was never recorded.
        ViewedAt ??= utc;
        Status = answer;
        RespondedAt = utc;
        Version++;
    }

    /// <summary>Brings the quote in line with <paramref name="content"/>. The items and days are replaced, not merged.</summary>
    private void Apply(QuoteContent content)
    {
        if (QuoteRules.CheckShape(content) is { Count: > 0 } problems)
        {
            throw new ArgumentException($"The quote cannot be saved: {problems[0].Message}", nameof(content));
        }

        Title = content.Title;
        ValidUntil = content.ValidUntil;
        Notes = content.Notes;

        _items.Clear();
        for (var i = 0; i < content.Items.Count; i++)
        {
            _items.Add(QuoteItem.Create(AgencyId, Id, i, content.Items[i]));
        }

        _itinerary.Clear();
        foreach (var day in content.Itinerary)
        {
            _itinerary.Add(QuoteItineraryDay.Create(AgencyId, Id, day));
        }

        TotalMinor = content.Total();
    }
}

/// <summary>One priced line of a quote: <c>crm.quote_items</c>. Written only through <see cref="Quote"/>.</summary>
public sealed class QuoteItem : Entity, ITenantScoped
{
    private QuoteItem()
    {
        Description = string.Empty;
    }

    internal static QuoteItem Create(Guid agencyId, Guid quoteId, int position, QuoteItemContent content) =>
        new()
        {
            AgencyId = agencyId,
            QuoteId = quoteId,
            Position = position,
            Description = content.Description,
            Quantity = content.Quantity,
            UnitPriceMinor = content.UnitPriceMinor,
            ProductId = content.ProductId,
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid QuoteId { get; private set; }

    /// <summary>Where the line sits in the agent's order, from 0.</summary>
    public int Position { get; private set; }

    public string Description { get; private set; }

    public int Quantity { get; private set; }

    public Money UnitPriceMinor { get; private set; }

    /// <summary>The catalog product it sells, when it is one of the agency's own.</summary>
    public Guid? ProductId { get; private set; }
}

/// <summary>One proposed day: <c>crm.quote_itinerary_days</c>. Written only through <see cref="Quote"/>.</summary>
public sealed class QuoteItineraryDay : Entity, ITenantScoped
{
    private QuoteItineraryDay()
    {
        Title = string.Empty;
        Description = string.Empty;
    }

    internal static QuoteItineraryDay Create(Guid agencyId, Guid quoteId, QuoteDayContent content) =>
        new()
        {
            AgencyId = agencyId,
            QuoteId = quoteId,
            DayNumber = content.DayNumber,
            Title = content.Title,
            Description = content.Description,
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid QuoteId { get; private set; }

    public int DayNumber { get; private set; }

    public string Title { get; private set; }

    public string Description { get; private set; }
}
