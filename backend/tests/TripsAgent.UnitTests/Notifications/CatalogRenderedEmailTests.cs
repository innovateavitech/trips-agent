using FluentAssertions;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Payments;

namespace TripsAgent.UnitTests.Notifications;

/// <summary>
/// Issue #45's M1 templates, all seven in the versioned catalog — including the two sent at once
/// because their variable is a live secret — with the refund notice the pipeline's reversals need, and
/// the pieces that carry the agency's logo and files.
/// </summary>
public class CatalogRenderedEmailTests
{
    [Fact]
    public void Every_M1_template_is_versioned_in_the_catalog()
    {
        NotificationTemplateCatalog.All.Select(t => t.Key).Should().Contain(
        [
            NotificationTemplateCatalog.IdentityVerifyEmail,
            NotificationTemplateCatalog.IdentityPasswordReset,
            NotificationTemplateCatalog.KybApproved,
            NotificationTemplateCatalog.KybRejected,
            NotificationTemplateCatalog.WalletTopUpReceipt,
            NotificationTemplateCatalog.BookingConfirmed,
            NotificationTemplateCatalog.BookingNeedsAttention,
            NotificationTemplateCatalog.BookingRefundNotice,
        ]);
    }

    [Fact]
    public void The_verification_email_renders_from_the_catalog_and_carries_the_code()
    {
        var email = VerificationEmail.Create("ada@example.test", "Ada", "482913", TimeSpan.FromMinutes(10));

        email.To.Should().Be("ada@example.test");
        email.Subject.Should().Be("482913 is your Trips Agent verification code");
        email.TextBody.Should().Contain("Hello Ada").And.Contain("482913").And.Contain("10 minutes");
        email.HtmlBody.Should().Contain("482913");
        email.FromName.Should().BeNull("an agency user's mail is ours, from our own sender");
    }

    [Fact]
    public void The_reset_link_is_encoded_in_the_html_and_left_whole_in_the_text()
    {
        const string link = "https://console.test/reset-password?token=abc&email=ada%40example.test";

        var email = PasswordResetEmail.Create("ada@example.test", "Ada", link, TimeSpan.FromMinutes(30));

        email.Subject.Should().Be("Reset your Trips Agent password");
        email.HtmlBody.Should().Contain("token=abc&amp;email=");
        email.TextBody.Should().Contain(link).And.Contain("30 minutes");
    }

    [Fact]
    public void A_name_typed_as_markup_arrives_as_text()
    {
        var email = VerificationEmail.Create("ada@example.test", "<a href=\"https://evil.test\">Ada</a>", "482913", TimeSpan.FromMinutes(10));

        email.HtmlBody.Should().NotContain("<a href=\"https://evil.test\">").And.Contain("&lt;a href=");
    }

    [Fact]
    public void A_traveller_template_is_never_sent_without_the_agencys_brand()
    {
        var render = () => SynchronousEmail.Render(
            NotificationTemplateCatalog.BookingConfirmed,
            "ada@example.test",
            "Ada",
            new Dictionary<string, string>(StringComparer.Ordinal));

        render.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("INotifier");
    }

    [Fact]
    public void A_logo_sent_inside_the_message_is_shown_from_its_content_id()
    {
        var template = NotificationTemplateCatalog.Find(NotificationTemplateCatalog.DocumentsIssued, NotificationChannel.Email)!.ToTemplate();
        var brand = new NotificationBrand("Lagos Travel", "#0A7E3B", NotificationRenderer.InlineLogoUrl, "12 Marina, Lagos", null);

        var rendered = NotificationRenderer.Render(
            template,
            brand,
            "Ada",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = "ORD-2026-000142",
                ["itinerarySummary"] = "Lagos (LOS) to Abuja (ABV)",
                ["documentList"] = "invoice INV-2026-000001, voucher VCH-2026-000001",
            });

        rendered.Html.Should().Contain("src=\"cid:agency-logo\"");
        rendered.Subject.Should().Be("Your booking documents — ORD-2026-000142");
        rendered.Text.Should().Contain("invoice INV-2026-000001, voucher VCH-2026-000001");
    }

    [Fact]
    public void One_item_needing_attention_is_said_plainly_and_asks_nothing_of_the_traveller()
    {
        var rendered = RenderForTraveller(
            NotificationTemplateCatalog.BookingNeedsAttention,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = "ORD-2026-000142",
                ["itemTitle"] = "Lagos (LOS) to Abuja (ABV)",
            });

        rendered.Subject.Should().Be("One item in your booking needs attention — ORD-2026-000142");
        Words(rendered.Text).Should()
            .Contain("needs attention: its ticket could not be issued")
            .And.Contain("Lagos Travel is looking into it")
            .And.Contain("You do not need to do anything right now");
    }

    [Fact]
    public void A_refund_notice_names_an_amount_only_when_it_went_back_to_the_travellers_card()
    {
        var toWallet = BookingEmails.DescribeRefund(new BookingRefund(Guid.CreateVersion7(), RefundMethod.WalletCredited, 110_750, "NGN"));
        var toCard = BookingEmails.DescribeRefund(new BookingRefund(Guid.CreateVersion7(), RefundMethod.Gateway, 110_750, "NGN"));

        toWallet.Should().NotContain("NGN", "the wallet got back what the agency paid, which is not the traveller's price")
            .And.Contain("If you have already paid for it");
        toCard.Should().Contain("NGN 1,107.50 has been refunded to the card you paid with");

        var rendered = RenderForTraveller(
            NotificationTemplateCatalog.BookingRefundNotice,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = "ORD-2026-000142",
                ["itemTitle"] = "Lagos (LOS) to Abuja (ABV)",
                ["refundDetail"] = toWallet,
            });

        rendered.Subject.Should().Be("An item in your booking has been cancelled — ORD-2026-000142");
        Words(rendered.Text).Should().Contain("could not be ticketed, so it has been cancelled").And.Contain(toWallet);
    }

    [Fact]
    public void A_notification_keeps_each_attachment_once()
    {
        var invoice = Guid.CreateVersion7();
        var voucher = Guid.CreateVersion7();

        var notification = Notification.Queue(
            Guid.CreateVersion7(),
            NotificationTemplateCatalog.DocumentsIssued,
            NotificationChannel.Email,
            NotificationTemplate.DefaultLocale,
            NotificationRecipientType.Traveller,
            "ada@example.test",
            "Ada",
            "{}",
            "documents.issued:1",
            attachmentAssetIds: [invoice, voucher, invoice]);

        notification.AttachmentAssetIds.Should().Equal(invoice, voucher);
    }

    [Fact]
    public void A_bounce_reported_after_sending_marks_it_bounced_without_counting_another_attempt()
    {
        var notification = Notification.Queue(
            Guid.CreateVersion7(), NotificationTemplateCatalog.KybApproved, NotificationChannel.Email,
            NotificationTemplate.DefaultLocale, NotificationRecipientType.AgencyUser, "owner@example.test", "Ngozi",
            "{}", "kyb.approved:1");

        notification.RecordBounceReport("before it was sent").Should().BeFalse("nothing was handed over yet");

        notification.MarkSending();
        notification.MarkSent(1, "<id@relay>", DateTimeOffset.UtcNow);
        notification.MarkDelivered(DateTimeOffset.UtcNow);

        notification.RecordBounceReport("550 5.1.1 user unknown").Should().BeTrue();

        notification.Status.Should().Be(NotificationStatus.Bounced);
        notification.DeliveredAt.Should().BeNull("a message the mailbox refused was not delivered");
        notification.Attempts.Should().Be(1);
        notification.RecordBounceReport("again").Should().BeFalse();
    }

    /// <summary>Renders a traveller template as Lagos Travel would send it — the renderer refuses one that names us.</summary>
    private static (string Subject, string Text) RenderForTraveller(string key, Dictionary<string, string> values)
    {
        var rendered = NotificationRenderer.Render(
            NotificationTemplateCatalog.Find(key, NotificationChannel.Email)!.ToTemplate(),
            new NotificationBrand("Lagos Travel", "#0A7E3B", NotificationRenderer.InlineLogoUrl, "12 Marina, Lagos", null),
            "Ada",
            values);

        return (rendered.Subject, rendered.Text);
    }

    /// <summary>The text with its line breaks and indents folded to single spaces.</summary>
    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
