using FluentAssertions;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Notifications;

namespace TripsAgent.UnitTests.Notifications;

/// <summary>
/// The catalog is code that humans edit, so these catch the edits that would otherwise only fail
/// when the dispatcher tried to send one — after the caller's transaction had committed.
/// </summary>
public class NotificationTemplateCatalogTests
{
    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var key in NotificationTemplateCatalog.All.Select(t => t.Key).Distinct(StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    [Fact]
    public void The_queued_M1_templates_are_all_there()
    {
        // Verify-email and password-reset are M1 templates too, but they carry a live secret and are
        // sent synchronously by VerificationEmail and PasswordResetEmail — see the issue #45 notes.
        NotificationTemplateCatalog.All.Select(t => t.Key).Should().Contain(
        [
            NotificationTemplateCatalog.KybApproved,
            NotificationTemplateCatalog.KybRejected,
            NotificationTemplateCatalog.WalletTopUpReceipt,
            NotificationTemplateCatalog.BookingConfirmed,
            NotificationTemplateCatalog.BookingNeedsAttention,
        ]);
    }

    [Fact]
    public void No_two_definitions_share_a_key_channel_locale_and_version()
    {
        NotificationTemplateCatalog.All
            .GroupBy(t => (t.Key, t.Channel, t.Locale, t.Version))
            .Where(group => group.Count() > 1)
            .Should().BeEmpty("the seeder would try to insert the same version twice");
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_token_a_template_uses_is_declared_or_supplied_by_the_renderer(string key)
    {
        var template = NotificationTemplateCatalog.Find(key, NotificationChannel.Email)!;
        var known = template.Tokens.Concat(NotificationTemplateCatalog.BrandTokens).ToHashSet(StringComparer.Ordinal);

        var used = NotificationRenderer.TokensIn(template.Subject + template.Html + template.Text);

        used.Should().OnlyContain(token => known.Contains(token),
            "an undeclared token passes the queue's check and then fails at send time");
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_declared_token_is_used(string key)
    {
        var template = NotificationTemplateCatalog.Find(key, NotificationChannel.Email)!;
        var used = NotificationRenderer.TokensIn(template.Subject + template.Html + template.Text);

        template.Tokens.Should().OnlyContain(token => used.Contains(token),
            "a declared token nobody renders is a caller made to supply something for nothing");
    }

    [Fact]
    public void No_traveller_template_mentions_us_even_before_it_is_filled()
    {
        foreach (var template in NotificationTemplateCatalog.All.Where(t => t.Audience == NotificationAudience.Traveller))
        {
            foreach (var part in new[] { template.Subject, template.Html, template.Text })
            {
                part.Should().NotContainEquivalentOf("Trips", $"{template.Key} goes to a traveller");
            }
        }
    }

    [Fact]
    public void Every_traveller_template_is_wrapped_in_the_agencys_brand()
    {
        foreach (var template in NotificationTemplateCatalog.All.Where(t => t.Audience == NotificationAudience.Traveller))
        {
            template.Html.Should().Contain("{{brandColor}}").And.Contain("{{brandName}}");
            template.Text.Should().Contain("{{brandName}}");
        }
    }

    [Fact]
    public void Find_returns_the_newest_version()
    {
        var found = NotificationTemplateCatalog.Find(NotificationTemplateCatalog.KybApproved, NotificationChannel.Email)!;

        found.Version.Should().Be(NotificationTemplateCatalog.All
            .Where(t => t.Key == NotificationTemplateCatalog.KybApproved)
            .Max(t => t.Version));
    }
}
