using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using MailKit.Net.Smtp;
using TripsAgent.Application.Notifications;
using TripsAgent.Infrastructure.Notifications;

namespace TripsAgent.UnitTests.Notifications;

/// <summary>
/// What the SMTP adapter makes of a relay's refusal — above all, whether it says the address is
/// dead. That decision suppresses an address for every agency on the platform, so a refusal that
/// is really about us (bad credentials, a blocklisted IP) must never be read as one.
/// </summary>
/// <remarks>
/// Against a tiny fake relay on a loopback socket rather than a mocked client, so the text being
/// classified is exactly what MailKit hands over from a real SMTP conversation.
/// </remarks>
public class SmtpEmailSenderTests
{
    [Theory]
    [InlineData("550 5.1.1 <ada@example.test>: Recipient address rejected: User unknown")]
    [InlineData("550 5.1.2 Bad destination system address")]
    [InlineData("550 5.1.10 Recipient address rejected: example.test publishes a null MX")]
    [InlineData("550 5.2.1 The email account that you tried to reach is disabled")]
    public async Task A_refusal_naming_the_address_as_bad_marks_it_undeliverable(string relayReply)
    {
        var rejected = await SendExpectingRejectionAsync(relayReply);

        rejected.AddressIsUndeliverable.Should().BeTrue();
    }

    [Theory]
    [InlineData("550 5.7.1 Relaying denied")]
    [InlineData("554 5.7.1 Service unavailable; Client host [203.0.113.7] blocked using a blocklist")]
    [InlineData("550 Requested action not taken: mailbox unavailable")]
    [InlineData("552 5.2.2 Mailbox full")]
    [InlineData("550 5.1.8 Bad sender's system address")]
    public async Task A_refusal_about_us_or_that_does_not_say_leaves_the_address_alone(string relayReply)
    {
        // Still a rejection — never retried — but nothing here proves the mailbox is gone. The
        // first is what every recipient gets while the relay credentials are wrong.
        var rejected = await SendExpectingRejectionAsync(relayReply);

        rejected.AddressIsUndeliverable.Should().BeFalse();
        rejected.Message.Should().Contain(relayReply[..3], "the relay's own words are what a person reads on the row");
    }

    [Fact]
    public async Task A_temporary_refusal_is_not_a_rejection_at_all()
    {
        await using var relay = new FakeRelay("450 4.2.1 Try again later");

        var act = () => Sender(relay).SendAsync(Message());

        // An ordinary failure: the dispatcher retries it with the broker's backoff.
        (await act.Should().ThrowAsync<SmtpCommandException>()).Which.Should().NotBeOfType<EmailRejectedException>();
    }

    [Fact]
    public async Task An_accepted_recipient_is_sent()
    {
        await using var relay = new FakeRelay("250 2.1.5 OK");

        var receipt = await Sender(relay).SendAsync(Message());

        receipt.ProviderMessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_pdf_travels_as_an_attachment_and_the_logo_inside_the_body()
    {
        await using var relay = new FakeRelay("250 2.1.5 OK");

        await Sender(relay).SendAsync(Message() with
        {
            Attachments =
            [
                new EmailAttachment("INV-2026-000001.pdf", "application/pdf", "%PDF-1.7 test"u8.ToArray()),
                new EmailAttachment("logo.png", "image/png", [0x89, 0x50, 0x4E, 0x47], ContentId: "agency-logo"),
            ],
        });

        var raw = await relay.Message;

        // The PDF is a file to keep; the logo is part of the HTML, referred to as cid:agency-logo.
        raw.Should().ContainEquivalentOf("multipart/mixed")
            .And.ContainEquivalentOf("multipart/related")
            .And.ContainEquivalentOf("filename=INV-2026-000001.pdf")
            .And.ContainEquivalentOf("application/pdf")
            .And.ContainEquivalentOf("Content-Id: <agency-logo>");
    }

    private static async Task<EmailRejectedException> SendExpectingRejectionAsync(string relayReply)
    {
        await using var relay = new FakeRelay(relayReply);

        var act = () => Sender(relay).SendAsync(Message());

        return (await act.Should().ThrowAsync<EmailRejectedException>()).Which;
    }

    private static SmtpEmailSender Sender(FakeRelay relay) =>
        new(new SmtpOptions { Host = IPAddress.Loopback.ToString(), Port = relay.Port });

    private static EmailMessage Message() =>
        new("ada@example.test", "Your agency is verified", "<p>Hello</p>", "Hello");

    /// <summary>
    /// Just enough SMTP for one message: greets, says yes to everything, and answers the recipient
    /// with the reply under test.
    /// </summary>
    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _recipientReply;
        private readonly Task _serving;
        private readonly TaskCompletionSource<string> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The raw message as the relay received it, once a DATA command has finished.</summary>
        public Task<string> Message => _message.Task;

        public FakeRelay(string recipientReply)
        {
            _recipientReply = recipientReply;
            _listener.Start();
            _serving = ServeOneAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();

            try
            {
                await _serving;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client hung up without QUIT, which is what MailKit does after a refusal.
            }

            _listener.Dispose();
        }

        private async Task ServeOneAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 relay.test ESMTP");

            while (await reader.ReadLineAsync() is { } line)
            {
                switch (line.Split(' ', 2)[0].ToUpperInvariant())
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-relay.test");
                        await writer.WriteLineAsync("250 ENHANCEDSTATUSCODES");
                        break;

                    case "RCPT":
                        await writer.WriteLineAsync(_recipientReply);
                        break;

                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } body && body != ".")
                        {
                            data.AppendLine(body);
                        }

                        // Before the reply, so the message is in hand by the time the sender returns.
                        _message.TrySetResult(data.ToString());
                        await writer.WriteLineAsync("250 2.0.0 Queued");
                        break;

                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;

                    default:
                        await writer.WriteLineAsync("250 2.0.0 OK");
                        break;
                }
            }
        }
    }
}
