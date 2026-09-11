namespace TripsAgent.Application.Notifications;

/// <summary>An email ready to send: who it goes to, and both renderings of the body.</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The rich version most mail clients show.</param>
/// <param name="TextBody">
/// The plain-text version. Required, not optional: some clients and most spam filters penalise
/// HTML-only mail, and a screen reader user may prefer it.
/// </param>
/// <param name="FromName">
/// The display name the recipient sees, when it must not be ours. A traveller's mail shows their
/// travel agency's name (CLAUDE.md rule 4); the address itself stays the platform's, because we
/// cannot sign mail for a domain whose DNS we do not control. Null means the configured default.
/// </param>
/// <param name="ReplyTo">
/// Where a reply should go. For traveller mail that is the agency's own inbox — a traveller who
/// replies must reach their agent, not us. Null means replies go to the From address.
/// </param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody,
    string? FromName = null,
    string? ReplyTo = null);

/// <summary>What the provider said when it accepted a message.</summary>
/// <param name="ProviderMessageId">
/// The provider's id for it, when there is one. Recorded against the notification so a bounce or
/// complaint report arriving days later can be matched back to the message that caused it.
/// </param>
public sealed record EmailReceipt(string? ProviderMessageId);

/// <summary>
/// The provider refused to deliver a message and there is no point trying again.
/// </summary>
/// <remarks>
/// A mailbox that does not exist, a domain that does not resolve, an address that has blocked us.
/// The distinction from an ordinary failure is the whole point: an ordinary failure — the relay is
/// down, the network blipped — is retried with backoff, while this one must not be. Retrying into
/// a hard bounce is how a sender's reputation is destroyed, and it would cost every agency on the
/// platform their deliverability.
/// </remarks>
public sealed class EmailRejectedException : Exception
{
    public EmailRejectedException(string message)
        : base(message)
    {
    }

    public EmailRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EmailRejectedException()
        : base("The email provider rejected the message permanently.")
    {
    }
}

/// <summary>
/// Sends email. A port, not a provider.
/// </summary>
/// <remarks>
/// The cloud provider is not chosen yet (CLAUDE.md), so nothing in the application may depend on
/// SES, SendGrid or Azure Communication Services directly. Locally this is SMTP into Mailpit; in
/// production it will be whichever adapter the infrastructure decision produces. Swapping it is
/// one registration, not a search-and-replace.
/// </remarks>
public interface IEmailSender
{
    /// <summary>Sends one message.</summary>
    /// <exception cref="EmailRejectedException">
    /// The address is permanently undeliverable. Callers must not retry; see the exception's own
    /// remarks.
    /// </exception>
    public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
