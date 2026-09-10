using FluentAssertions;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Domain;

public class EntityTests
{
    private sealed class Agency : Entity
    {
        public Agency() { }

        public Agency(Guid id) : base(id) { }
    }

    private sealed class Booking : Entity
    {
        public Booking(Guid id) : base(id) { }
    }

    [Fact]
    public void A_new_entity_gets_a_version_7_identity()
    {
        var entity = new Agency();

        entity.Id.Should().NotBe(Guid.Empty);

        // Version 7 puts a millisecond timestamp in the high bits, which is what keeps inserts
        // roughly sequential and stops the primary-key index fragmenting the way v4 does.
        entity.Id.Version.Should().Be(7);
    }

    [Fact]
    public async Task Identities_created_later_sort_after_earlier_ones()
    {
        var first = new Agency().Id;

        // Version 7 orders by a millisecond timestamp; within a single millisecond the
        // remaining bits are random and the order is deliberately unspecified. Asserting
        // ordering on two ids created back-to-back would be a coin flip, so put a real gap
        // between them — the property that matters is locality over time, not per-call
        // monotonicity.
        await Task.Delay(5);

        var second = new Agency().Id;

        // Compared as UUID strings: for version 7 the timestamp occupies the leading
        // characters, so lexical order matches chronological order.
        string.CompareOrdinal(first.ToString(), second.ToString())
            .Should().BeLessThan(0);
    }

    [Fact]
    public void Two_instances_with_the_same_id_are_equal()
    {
        var id = Guid.CreateVersion7();

        // The same row loaded by two different DbContexts is the same thing.
        new Agency(id).Should().Be(new Agency(id));
        new Agency(id).GetHashCode().Should().Be(new Agency(id).GetHashCode());
    }

    [Fact]
    public void Different_ids_are_not_equal() =>
        new Agency(Guid.CreateVersion7()).Should().NotBe(new Agency(Guid.CreateVersion7()));

    [Fact]
    public void Different_types_sharing_an_id_are_not_equal()
    {
        var id = Guid.CreateVersion7();

        // An agency and a booking that happen to share an id are still different things.
        new Agency(id).Equals(new Booking(id)).Should().BeFalse();
    }

    [Fact]
    public void An_entity_with_an_empty_id_is_never_equal_to_another()
    {
        // Two unsaved entities are distinct until they have real identities; treating them as
        // equal would silently collapse them in a HashSet.
        new Agency(Guid.Empty).Equals(new Agency(Guid.Empty)).Should().BeFalse();
    }

    [Fact]
    public void An_entity_is_not_equal_to_null() =>
        new Agency().Equals(null).Should().BeFalse();

    [Fact]
    public void An_entity_is_not_equal_to_an_unrelated_object() =>
        new Agency().Equals("not an entity").Should().BeFalse();
}
