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

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

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

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
