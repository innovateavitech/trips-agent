using FluentAssertions;
using TripsAgent.Application.Crm;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Crm;

namespace TripsAgent.UnitTests.Crm;

/// <summary>
/// The CRM's rules, without a database: what an inquiry and a quote must say, what a lead's stages
/// allow, and what a sent quote will and will not take (#62).
/// </summary>
public class CrmRuleTests
{
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 12);

    /// <summary>One Nigerian mobile number, written the four ways people write it.</summary>
    private static readonly string[] OnePhoneNumber =
        ["+234 803 000 1122", "0803 000 1122", "234-803-000-1122", "(234) 803.000.1122"];

    // ------------------------------------------------------------------ what an inquiry must say

    [Fact]
    public void An_inquiry_needs_a_destination_and_at_least_one_adult()
    {
        Fields(LeadRules.Validate(Details(destination: "   "))).Should().Contain("destination");
        Fields(LeadRules.Validate(Details(adults: 0))).Should().Contain("adults");
        Fields(LeadRules.Validate(Details(children: -1))).Should().Contain("children");
        Fields(LeadRules.Validate(Details())).Should().BeEmpty();
    }

    [Fact]
    public void A_trip_cannot_end_before_it_starts_and_a_budget_cannot_run_backwards()
    {
        Fields(LeadRules.Validate(Details(from: Today.AddDays(10), to: Today.AddDays(3))))
            .Should().Contain("travelTo");

        Fields(LeadRules.Validate(Details(min: new Money(500_00), max: new Money(100_00))))
            .Should().Contain("budgetMaxMinor");

        // An open end on either side is what most people give, and it is fine.
        Fields(LeadRules.Validate(Details(from: Today.AddDays(10), to: null))).Should().BeEmpty();
    }

    [Fact]
    public void An_inquiry_needs_a_name_and_a_way_to_reach_them()
    {
        Fields(ContactDetails.Check("Chiamaka Okonkwo", null, null, string.Empty)).Should().Contain("email");
        Fields(ContactDetails.Check(" ", "chiamaka@example.test", null, string.Empty)).Should().Contain("name");
        Fields(ContactDetails.Check("Chiamaka Okonkwo", "not-an-address", null, string.Empty)).Should().Contain("email");

        Fields(ContactDetails.Check("Chiamaka Okonkwo", "chiamaka@example.test", null, string.Empty)).Should().BeEmpty();
        Fields(ContactDetails.Check("Chiamaka Okonkwo", null, "+234 803 000 1122", string.Empty)).Should().BeEmpty();

        // The prefix is what the field is called in the request the check is for.
        Fields(ContactDetails.Check(null, null, null, "customer.")).Should().Contain("customer.name");
    }

    [Fact]
    public void One_phone_number_written_four_ways_is_one_number()
    {
        var keys = OnePhoneNumber.Select(ContactDetails.PhoneKey).Distinct();

        keys.Should().ContainSingle("a customer who writes their number differently is still the same customer");
    }

    // ------------------------------------------------------------------ the pipeline

    [Fact]
    public void A_lead_opens_at_new_with_its_first_stage_written_to_its_history()
    {
        var lead = Open();

        lead.Stage.Should().Be(LeadStage.New);
        lead.IsOpen.Should().BeTrue();
        lead.History.Should().ContainSingle();
        lead.History[0].Stage.Should().Be(LeadStage.New);
        lead.History[0].ByName.Should().Be("Ada Obi");
    }

    [Fact]
    public void Losing_a_lead_needs_a_reason_and_moving_it_on_again_forgets_it()
    {
        Fields(LeadRules.CheckMove(LeadStage.Lost, "  ")).Should().Contain("reason");
        Fields(LeadRules.CheckMove(LeadStage.Lost, "Booked elsewhere")).Should().BeEmpty();
        Fields(LeadRules.CheckMove(LeadStage.Won, null)).Should().BeEmpty();

        var lead = Open();
        lead.MoveTo(LeadStage.Lost, " Booked elsewhere ", null, "Ada Obi", Now);
        lead.LostReason.Should().Be("Booked elsewhere");

        lead.MoveTo(LeadStage.Negotiating, null, null, "Ada Obi", Now);
        lead.LostReason.Should().BeNull("the reason no longer holds once they are talking again");
        lead.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void A_lead_is_never_moved_to_the_stage_it_is_already_at()
    {
        var lead = Open();

        var moving = () => lead.MoveTo(LeadStage.New, null, null, "Ada Obi", Now);

        moving.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sending_a_quote_moves_a_new_lead_on_and_leaves_a_later_one_where_it_is()
    {
        var newLead = Open();
        newLead.MarkQuoted(null, "Ada Obi", Now).Should().BeTrue();
        newLead.Stage.Should().Be(LeadStage.Quoted);

        var negotiating = Open();
        negotiating.MoveTo(LeadStage.Negotiating, null, null, "Ada Obi", Now);

        negotiating.MarkQuoted(null, "Ada Obi", Now).Should().BeFalse();
        negotiating.Stage.Should().Be(LeadStage.Negotiating, "a second quote does not move them backwards");
    }

    // ------------------------------------------------------------------ quotes

    [Fact]
    public void A_quote_is_totalled_from_its_items_and_numbered_from_one()
    {
        var quote = Draft();

        quote.QuoteNumber.Should().Be("QT-0001");
        quote.Status.Should().Be(QuoteStatus.Draft);
        quote.TotalMinor.AmountMinor.Should().Be((2 * 45_000_000L) + 7_500_000L);
        quote.PublicToken.Should().BeNull();

        Quote.FormatNumber(10_000).Should().Be("QT-10000", "the number never wraps");
    }

    [Fact]
    public void A_quote_needs_a_title_items_and_a_date_that_has_not_passed()
    {
        Fields(QuoteRules.Validate(Content(title: " "), Today)).Should().Contain("title");
        Fields(QuoteRules.Validate(Content(items: []), Today)).Should().Contain("items");
        Fields(QuoteRules.Validate(Content(validUntil: Today.AddDays(-1)), Today)).Should().Contain("validUntil");
        Fields(QuoteRules.Validate(Content(), Today)).Should().BeEmpty();
    }

    [Fact]
    public void A_draft_changes_as_often_as_the_agent_likes_and_a_sent_quote_never_again()
    {
        var quote = Draft();

        quote.Revise(Content(title: "Dubai, ten nights"));
        quote.Title.Should().Be("Dubai, ten nights");

        quote.Send("a-token", Today, Now);

        quote.Status.Should().Be(QuoteStatus.Sent);
        quote.SentAt.Should().Be(Now);
        quote.PublicToken.Should().Be("a-token");

        var revising = () => quote.Revise(Content(title: "Dubai, twelve nights"));

        revising.Should().Throw<InvalidOperationException>().WithMessage("*sent quote cannot be changed*");
    }

    [Fact]
    public void A_quote_with_nothing_priced_on_it_cannot_be_sent()
    {
        QuoteRules.WhyNotSendable(Draft(), Today).Should().BeNull();

        // A quote drafted last month, sent today: the date has to move first. An itemless quote
        // cannot get this far — the draft itself is refused.
        QuoteRules.WhyNotSendable(Draft(Content(validUntil: Today.AddDays(3))), Today.AddDays(10))
            .Should().Contain("expired");

        var sent = Draft();
        sent.Send("a-token", Today, Now);

        QuoteRules.WhyNotSendable(sent, Today).Should().Contain("already been sent");
    }

    [Fact]
    public void Only_the_first_view_counts()
    {
        var quote = Draft();
        quote.Send("a-token", Today, Now);

        quote.RecordView(Now).Should().BeTrue();
        quote.Status.Should().Be(QuoteStatus.Viewed);
        quote.ViewedAt.Should().Be(Now);

        quote.RecordView(Now.AddHours(2)).Should().BeFalse();
        quote.ViewedAt.Should().Be(Now, "a customer who reads it four times has not seen it four times");
    }

    [Fact]
    public void A_quote_is_answered_once_and_reads_as_expired_after_its_last_valid_day()
    {
        var quote = Draft();
        quote.Send("a-token", Today, Now);

        quote.WhyNotAnswerable(Today).Should().BeNull();
        quote.Accept(Today, Now);

        quote.Status.Should().Be(QuoteStatus.Accepted);
        quote.RespondedAt.Should().Be(Now);
        quote.WhyNotAnswerable(Today).Should().Contain("already been accepted");

        var stale = Draft();
        stale.Send("another-token", Today, Now);

        var afterwards = stale.ValidUntil.AddDays(1);

        stale.StatusOn(afterwards).Should().Be(QuoteStatus.Expired);
        stale.WhyNotAnswerable(afterwards).Should().Contain("Ask for a new one");

        // Expiry is how it reads, not what it is: the stored status is still Sent.
        stale.Status.Should().Be(QuoteStatus.Sent);
    }

    // ------------------------------------------------------------------ the pieces around them

    [Fact]
    public void An_enum_arrives_as_its_name_and_nothing_else()
    {
        EnumNames.Parse<LeadStage>("Quoted").Should().Be(LeadStage.Quoted);
        EnumNames.Parse<LeadStage>("quoted").Should().Be(LeadStage.Quoted, "the console's casing is not a rejection");

        EnumNames.Parse<LeadStage>("3").Should().BeNull("a number is not a name");
        EnumNames.Parse<LeadStage>("New,Won").Should().BeNull("two names ORed together are nobody's stage");
        EnumNames.Parse<LeadStage>("Elsewhere").Should().BeNull();
        EnumNames.Parse<LeadStage>(null).Should().BeNull();
    }

    [Fact]
    public void A_quote_link_token_is_unguessable_and_anything_else_is_turned_away()
    {
        var token = QuoteLinkTokens.New();

        token.Should().HaveLength(QuoteLinkTokens.Length);
        QuoteLinkTokens.LooksValid(token).Should().BeTrue();
        QuoteLinkTokens.New().Should().NotBe(token);

        QuoteLinkTokens.LooksValid("short").Should().BeFalse();
        QuoteLinkTokens.LooksValid(new string('!', QuoteLinkTokens.Length)).Should().BeFalse();
        QuoteLinkTokens.LooksValid(null).Should().BeFalse();
    }

    [Fact]
    public void Money_in_an_email_is_written_from_whole_minor_units()
    {
        CrmFormat.Money(360_000_000L, "NGN").Should().Be("₦3,600,000.00");
        CrmFormat.Money(7_550L, "NGN").Should().Be("₦75.50");
        CrmFormat.Money(125_000L, "USD").Should().Be("USD 1,250.00");
        CrmFormat.Day(new DateOnly(2026, 9, 18)).Should().Be("18 September 2026");
    }

    [Fact]
    public void A_storefront_host_is_read_without_its_port_or_its_casing()
    {
        StorefrontCrmService.NormaliseHost("Lekki-Horizon.com:443").Should().Be("lekki-horizon.com");
        StorefrontCrmService.NormaliseHost(" lekki-horizon.com. ").Should().Be("lekki-horizon.com");
        StorefrontCrmService.NormaliseHost("  ").Should().BeNull();
        StorefrontCrmService.NormaliseHost(null).Should().BeNull();
    }

    // ------------------------------------------------------------------ samples

    private static List<string> Fields(IReadOnlyList<CrmProblem> problems) =>
        problems.Select(problem => problem.Field).ToList();

    private static LeadDetails Details(
        string destination = "Dubai",
        DateOnly? from = null,
        DateOnly? to = null,
        int adults = 2,
        int children = 1,
        Money? min = null,
        Money? max = null) =>
        new LeadDetails(destination, from, to, adults, children, min, max, "Somewhere warm in March.").Normalised();

    private static Lead Open() =>
        Lead.Open(Agency, Customer, LeadSource.Manual, Details(), "NGN", null, "Ada Obi", Now);

    private static QuoteContent Content(
        string title = "Dubai, seven nights",
        DateOnly? validUntil = null,
        IReadOnlyList<QuoteItemContent>? items = null) =>
        new QuoteContent(
            title,
            validUntil ?? Today.AddDays(14),
            items ??
            [
                new QuoteItemContent("Hotel, two nights", 2, new Money(45_000_000L), null),
                new QuoteItemContent("Airport transfer", 1, new Money(7_500_000L), null),
            ],
            [new QuoteDayContent(1, "Arrive", "Transfer to the hotel.")],
            "Prices hold until the date above.").Normalised();

    private static Quote Draft(QuoteContent? content = null) =>
        Quote.Draft(Agency, Guid.CreateVersion7(), 1, "NGN", content ?? Content(), null);
}
