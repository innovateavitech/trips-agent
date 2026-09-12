using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>The email a customer gets when their quote is sent (#62).</summary>
/// <remarks>
/// Its own port so <see cref="QuoteService"/> can be tested without a notification pipeline, and so
/// there is one place holding what a traveller-facing quote email may say.
/// </remarks>
public interface IQuoteEmails
{
    /// <summary>
    /// Stages "here is your quote" for <paramref name="customer"/>, to commit with the caller's save.
    /// </summary>
    /// <param name="quote">The quote, already sent — its number, total and last valid day.</param>
    /// <param name="customer">Who it goes to. A customer with no email address is not emailed.</param>
    /// <param name="publicUrl">The customer's link, on the agency's own storefront.</param>
    /// <param name="actor">The agency and the person sending it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>False when there was nobody to email, or the same quote's email is already queued.</returns>
    public Task<bool> QueueQuoteSentAsync(
        Quote quote,
        Customer customer,
        string publicUrl,
        CrmActor actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Queues the quote email through the notification dispatcher, in the agency's branding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here names Trips</b> (CLAUDE.md rule 4). The template is traveller-facing, so the
/// dispatcher renders it in the agency's brand and sends it with the agency's trading name as the
/// display name and the agency's own address as reply-to, from the neutral sending domain
/// (decision 19). The link is to the agency's own storefront.
/// </para>
/// <para>
/// Staged, not sent: the row and its outbox message join the caller's unit of work, so a customer is
/// never emailed a link to a quote whose send did not commit.
/// </para>
/// </remarks>
public sealed class QuoteEmails : IQuoteEmails
{
    private readonly INotifier _notifier;

    public QuoteEmails(INotifier notifier) => _notifier = notifier;

    /// <summary>What makes the email for a quote the same email: one per quote, however often it is retried.</summary>
    public static string DedupeKeyFor(Guid quoteId) => $"{NotificationTemplateCatalog.CrmQuoteSent}:{quoteId}";

    public async Task<bool> QueueQuoteSentAsync(
        Quote quote,
        Customer customer,
        string publicUrl,
        CrmActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicUrl);

        // A customer known only by phone has nothing to email. The quote is still sent, and the agent
        // has its link to pass on however they reached the customer in the first place.
        if (customer.Email is not { Length: > 0 } address)
        {
            return false;
        }

        return await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                actor.AgencyId,
                NotificationTemplateCatalog.CrmQuoteSent,
                address,
                customer.Name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["quoteNumber"] = quote.QuoteNumber,
                    ["quoteTitle"] = quote.Title,
                    ["quoteTotal"] = CrmFormat.Money(quote.TotalMinor.AmountMinor, quote.Currency),
                    ["validUntil"] = CrmFormat.Day(quote.ValidUntil),
                    ["quoteUrl"] = publicUrl,
                },
                DedupeKeyFor(quote.Id)),
            cancellationToken);
    }
}
