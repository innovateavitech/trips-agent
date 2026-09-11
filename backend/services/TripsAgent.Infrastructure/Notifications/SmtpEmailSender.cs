using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>Where and how to deliver mail over SMTP.</summary>
public sealed class SmtpOptions
{
    public string Host { get; init; } = "localhost";

    /// <summary>1025 is Mailpit's SMTP port; production relays are usually 587.</summary>
    public int Port { get; init; } = 1025;

    /// <summary>
    /// <c>None</c> for Mailpit, which speaks plain SMTP on localhost. <c>StartTls</c> for a real
    /// relay — never <c>None</c> across a network, where credentials would travel in the clear.
    /// </summary>
    public SecureSocketOptions SecureSocket { get; init; } = SecureSocketOptions.None;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string FromAddress { get; init; } = "no-reply@tripsagent.test";

    public string FromName { get; init; } = "Trips Agent";

    /// <summary>
    /// The address traveller-facing mail is sent from, under the agency's display name. Null
    /// falls back to <see cref="FromAddress"/>.
    /// </summary>
    /// <remarks>
    /// Separate because <see cref="FromAddress"/> is ours, and a traveller who looks past the
    /// display name at the address must not find our domain there (CLAUDE.md rule 4). It has to be
    /// a domain we can sign mail for, so it cannot be the agency's own until custom sending domains
    /// exist; a neutral one is the honest middle ground.
    /// </remarks>
    public string? WhiteLabelFromAddress { get; init; }
}

/// <summary>
/// Delivers email over SMTP. Locally that means Mailpit, which catches everything and shows it at
/// <c>http://localhost:8025</c> without ever reaching a real inbox.
/// </summary>
/// <remarks>
/// Plain SMTP on purpose. The cloud provider is undecided (CLAUDE.md), and every serious email
/// service accepts SMTP, so this adapter will work unchanged against whichever is chosen. A
/// provider-specific adapter can replace it later behind the same <see cref="IEmailSender"/> port.
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;

    public SmtpEmailSender(SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mime = new MimeMessage();

        // A message with its own display name is sent on somebody else's behalf — a travel agency
        // writing to its traveller — so it goes from the neutral address rather than ours.
        var fromAddress = message.FromName is null
            ? _options.FromAddress
            : _options.WhiteLabelFromAddress ?? _options.FromAddress;

        mime.From.Add(new MailboxAddress(message.FromName ?? _options.FromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        // Ours rather than the relay's, so we know it before sending and can record it: a bounce
        // report that arrives later names this id.
        mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(DomainOf(fromAddress));

        // Both parts, so a client that cannot or will not render HTML still gets a readable email.
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        using var client = new SmtpClient();

        await client.ConnectAsync(_options.Host, _options.Port, _options.SecureSocket, cancellationToken);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken);
        }

        try
        {
            await client.SendAsync(mime, cancellationToken);
        }
        catch (SmtpCommandException ex) when (IsPermanentRecipientFailure(ex))
        {
            // A 5xx on the recipient — no such mailbox, domain does not exist. Retrying cannot help
            // and damages our reputation with the relay; the dispatcher suppresses the address.
            throw new EmailRejectedException($"The relay refused {ex.Mailbox?.Address ?? message.To}: {ex.Message}", ex);
        }

        await client.DisconnectAsync(quit: true, cancellationToken);

        return new EmailReceipt(mime.MessageId);
    }

    private static bool IsPermanentRecipientFailure(SmtpCommandException ex) =>
        ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)ex.StatusCode >= 500;

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : "localhost";
    }
}
