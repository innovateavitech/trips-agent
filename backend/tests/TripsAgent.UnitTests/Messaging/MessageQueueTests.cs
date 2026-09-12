using FluentAssertions;
using TripsAgent.Application.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// The queue catalog is vocabulary, not logic, so these tests mostly assert that it still says
/// what the delivery plan says. That sounds trivial until someone renames a queue in code without
/// renaming it on the broker, and messages start landing somewhere nothing reads.
/// </summary>
public class MessageQueueTests
{
    /// <summary>
    /// Copied by hand from docs/ARCHITECTURE_AND_DELIVERY_PLAN.md §3. Deliberately duplicated
    /// rather than derived from MessageQueue.All — a test that reads its expectations out of the
    /// thing it is testing cannot fail.
    /// </summary>
    private static readonly string[] QueuesFromThePlan =
    [
        "booking.saga",
        "supplier.poll",
        "payments.webhook",
        "payments.reversal",
        "notifications.email",
        "notifications.sms",
        "documents.render",
        "media.process",
        "reports.generate",
        "domains.provision",
        "analytics.rollup",
    ];

    [Fact]
    public void All_should_be_exactly_the_queues_the_plan_names()
    {
        MessageQueue.All.Select(queue => queue.Name)
            .Should().BeEquivalentTo(QueuesFromThePlan);
    }

    [Fact]
    public void All_should_not_contain_a_null()
    {
        // Static property initialisers run top to bottom, so a queue declared *below* All is null
        // by the time All is built — and stays null forever, with no error. Moving a property is
        // an easy and completely silent way to break this file; this is the test that catches it.
        MessageQueue.All.Should().NotContainNulls();
    }

    [Fact]
    public void Queue_names_should_be_unique()
    {
        MessageQueue.All.Select(queue => queue.Name)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_queue_should_have_its_own_dead_letter_queue()
    {
        MessageQueue.All.Select(queue => queue.DeadLetterName)
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(name => name.EndsWith(MessageQueue.DeadLetterSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void DeadLetterName_should_be_the_queue_name_plus_the_suffix()
    {
        // This has to match what MassTransit actually creates on the broker. If MassTransit ever
        // changes its convention, this is the one place that has to change with it.
        MessageQueue.BookingSaga.DeadLetterName.Should().Be("booking.saga_error");
    }

    [Fact]
    public void ToString_should_be_the_wire_name()
    {
        // Log lines and RabbitMQ's management UI should agree on what a queue is called.
        MessageQueue.SupplierPoll.ToString().Should().Be("supplier.poll");
    }
}
