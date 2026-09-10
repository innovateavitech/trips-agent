namespace TripsAgent.Application.Notifications;

/// <summary>An email ready to send: who it goes to, and both renderings of the body.</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The rich version most mail clients show.</param>
/// <param name="TextBody">
/// The plain-text version. Required, not optional: some clients and most spam filters penalise
/// HTML-only mail, and a screen reader user may prefer it.
/// </param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

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
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
