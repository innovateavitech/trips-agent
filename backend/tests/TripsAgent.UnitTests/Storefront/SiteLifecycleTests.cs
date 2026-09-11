using FluentAssertions;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// Draft, stage, publish, roll back: the version rules that make a rollback safe, and the publish gate
/// for open question 11.
/// </summary>
public class SiteLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly Guid Template = Guid.CreateVersion7();

    [Fact]
    public void A_new_site_has_a_draft_numbered_zero_and_announces_itself()
    {
        var site = Site.Create(Agency, Template, "Lekki Horizon Travels");
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        draft.VersionNumber.Should().Be(SiteVersion.DraftNumber);
        draft.Status.Should().Be(SiteVersionStatus.Draft);
        site.Status.Should().Be(SiteStatus.Draft);
        site.PullDomainEvents().Should().ContainSingle().Which.Should().BeOfType<SiteCreated>();
    }

    [Fact]
    public void Publishing_the_first_staged_version_puts_the_site_live()
    {
        var (site, _) = NewSite();
        var staged = Stage(site, 1);

        site.Publish(staged, null, userId: null, Now);

        site.Status.Should().Be(SiteStatus.Published);
        site.PublishedVersionId.Should().Be(staged.Id);
        staged.Status.Should().Be(SiteVersionStatus.Published);
        site.PullDomainEvents().OfType<SitePublished>().Should().ContainSingle()
            .Which.Should().Match<SitePublished>(published => !published.IsRollback && published.PreviousVersionId == null);
    }

    [Fact]
    public void Publishing_again_archives_the_live_version_so_it_can_come_back()
    {
        var (site, _) = NewSite();
        var first = Stage(site, 1);
        site.Publish(first, null, null, Now);

        var second = Stage(site, 2);
        site.Publish(second, first, null, Now.AddHours(1));

        first.Status.Should().Be(SiteVersionStatus.Archived);
        first.CanBeRolledBackTo.Should().BeTrue();
        site.PublishedVersionId.Should().Be(second.Id);
    }

    [Fact]
    public void Rolling_back_moves_the_pointer_and_says_it_was_a_rollback()
    {
        var (site, _) = NewSite();
        var first = Stage(site, 1);
        site.Publish(first, null, null, Now);
        var second = Stage(site, 2);
        site.Publish(second, first, null, Now.AddHours(1));
        site.PullDomainEvents();

        site.RollBackTo(first, second, null, Now.AddHours(2));

        site.PublishedVersionId.Should().Be(first.Id);
        first.Status.Should().Be(SiteVersionStatus.Published);
        second.Status.Should().Be(SiteVersionStatus.Archived);
        site.PullDomainEvents().OfType<SitePublished>().Should().ContainSingle()
            .Which.IsRollback.Should().BeTrue();
    }

    [Fact]
    public void A_staging_that_was_never_live_cannot_be_rolled_back_to()
    {
        var (site, _) = NewSite();
        var first = Stage(site, 1);
        site.Publish(first, null, null, Now);

        var superseded = Stage(site, 2);
        superseded.Supersede(Now);

        superseded.CanBeRolledBackTo.Should().BeFalse();
        var act = () => site.RollBackTo(superseded, first, null, Now);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Only_the_staged_version_can_be_published()
    {
        var (site, draft) = NewSite();

        var act = () => site.Publish(draft, null, null, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_current_version_handed_in_must_be_the_live_one()
    {
        var (site, _) = NewSite();
        var first = Stage(site, 1);
        site.Publish(first, null, null, Now);
        var second = Stage(site, 2);
        var stranger = Stage(site, 3);

        var act = () => site.Publish(second, stranger, null, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(true, 1, false, true)]
    [InlineData(true, 0, true, true)]
    [InlineData(true, 0, false, false)]
    public void Something_to_sell_means_a_published_product_or_flight_sales(bool verified, int products, bool flights, bool allowed)
    {
        var facts = new PublishGateFacts(verified, products, flights);

        SomethingToSellRule.IsSatisfiedBy(facts).Should().Be(allowed);
        SitePublishGate.Check(facts).Should().HaveCount(allowed ? 0 : 1);
    }

    [Fact]
    public void An_unverified_agency_cannot_publish_and_is_told_every_reason()
    {
        var problems = SitePublishGate.Check(new PublishGateFacts(AgencyIsVerified: false, PublishedProductCount: 0, FlightSearchEnabled: false));

        problems.Select(problem => problem.Code).Should().Equal(SitePublishGate.AgencyNotVerified, SitePublishGate.NothingToSell);
    }

    private static (Site Site, SiteVersion Draft) NewSite()
    {
        var site = Site.Create(Agency, Template, "Lekki Horizon Travels");
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);
        site.PullDomainEvents();
        return (site, draft);
    }

    private static SiteVersion Stage(Site site, int number) =>
        SiteVersion.Stage(site, number, """{"schemaVersion":1}""", """{"schemaVersion":1}""", null, Now);
}
