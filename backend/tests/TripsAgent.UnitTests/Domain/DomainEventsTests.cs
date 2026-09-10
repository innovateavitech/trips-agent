using FluentAssertions;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Domain;

public class DomainEventsTests
{
    private sealed record ProbeRaised(Guid AggregateId) : IDomainEvent;

    private sealed class ProbeAggregate : AggregateRoot
    {
        public void DoSomething() => Raise(new ProbeRaised(Id));

        public void DoTwoThings()
        {
            Raise(new ProbeRaised(Id));
            Raise(new ProbeRaised(Id));
        }
    }

    [Fact]
    public void A_freshly_created_aggregate_has_no_pending_events()
    {
        var aggregate = new ProbeAggregate();

        aggregate.PullDomainEvents().Should().BeEmpty();
    }

    [Fact]
    public void Raise_queues_the_event_for_the_next_pull()
    {
        var aggregate = new ProbeAggregate();

        aggregate.DoSomething();

        aggregate.PullDomainEvents().Should().ContainSingle()
            .Which.Should().BeOfType<ProbeRaised>()
            .Which.AggregateId.Should().Be(aggregate.Id);
    }

    [Fact]
    public void Raise_preserves_the_order_events_were_raised_in()
    {
        var aggregate = new ProbeAggregate();

        aggregate.DoTwoThings();

        aggregate.PullDomainEvents().Should().HaveCount(2);
    }

    [Fact]
    public void Pulling_forgets_the_events_so_a_second_save_does_not_republish_them()
    {
        var aggregate = new ProbeAggregate();
        aggregate.DoSomething();

        aggregate.PullDomainEvents();
        var secondPull = aggregate.PullDomainEvents();

        secondPull.Should().BeEmpty();
    }

    [Fact]
    public void Events_raised_after_a_pull_are_queued_fresh()
    {
        var aggregate = new ProbeAggregate();
        aggregate.DoSomething();
        aggregate.PullDomainEvents();

        aggregate.DoSomething();

        aggregate.PullDomainEvents().Should().ContainSingle();
    }
}
