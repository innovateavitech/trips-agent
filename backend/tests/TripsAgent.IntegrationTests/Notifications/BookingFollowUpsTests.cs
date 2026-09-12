using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Messaging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Payments;
using TripsAgent.IntegrationTests.Checkout;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Notifications;

/// <summary>
/// The traveller's side of the booking pipeline's events against real PostgreSQL (#42–#46): who is
/// written to, what they are told, and that an event delivered three times writes once.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BookingFollowUpsTests : IAsyncLifetime
{
    private static readonly string[] TravellerTemplates =
    [
        NotificationTemplateCatalog.BookingConfirmed,
        NotificationTemplateCatalog.BookingNeedsAttention,
        NotificationTemplateCatalog.BookingRefundNotice,
    ];

    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public BookingFollowUpsTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task A_confirmed_booking_emails_its_lead_traveller_once_and_asks_for_its_documents()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        await PaymentReversalTests.ConfirmedBookingAsync(harness);
        var confirmed = (await harness.OutboxAsync<BookingConfirmed>()).Should().ContainSingle().Subject;

        // The broker redelivers, and a job can run twice: each run after the first finds its email queued.
        for (var delivery = 0; delivery < 3; delivery++)
        {
            await FollowUpAsync(harness, followUps => followUps.ConfirmedAsync(confirmed));
        }

        var email = (await TravellerEmailsAsync(harness)).Should().ContainSingle().Subject;
        email.TemplateKey.Should().Be(NotificationTemplateCatalog.BookingConfirmed);
        email.RecipientAddress.Should().Be("ngozi@example.test", "the lead traveller's address, as the agent typed it at checkout");
        email.RecipientName.Should().Be("Ngozi Adeyemi");
        email.Payload.Should().Contain(confirmed.OrderNumber);

        (await harness.OutboxAsync<OrderDocumentsRequested>()).Should().NotBeEmpty()
            .And.OnlyContain(request => request.OrderId == confirmed.OrderId
                                        && request.RecipientName == "Ngozi Adeyemi"
                                        && request.RecipientEmail == "ngozi@example.test");
    }

    [Fact]
    public async Task A_failed_item_is_explained_plainly_and_its_refund_notice_names_no_agency_price()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var (_, request) = await PaymentReversalTests.ReversalRequestedAsync(harness, issueStatus: 200);
        await harness.ReverseAsync(request);

        var flagged = (await harness.OutboxAsync<BookingNeedsResolution>()).Should().ContainSingle().Subject;
        var reversed = (await harness.OutboxAsync<PaymentReversed>()).Should().ContainSingle().Subject;

        for (var delivery = 0; delivery < 3; delivery++)
        {
            await FollowUpAsync(harness, followUps => followUps.NeedsResolutionAsync(flagged));
            await FollowUpAsync(harness, followUps => followUps.PaymentReversedAsync(reversed));
        }

        var emails = await TravellerEmailsAsync(harness);
        emails.Select(email => email.TemplateKey).Should().BeEquivalentTo(
            [NotificationTemplateCatalog.BookingNeedsAttention, NotificationTemplateCatalog.BookingRefundNotice]);
        emails.Should().OnlyContain(email => email.RecipientAddress == "ngozi@example.test");

        // The pipeline's reason is written for the agent, and names the supplier. None of it reaches the traveller.
        flagged.Reason.Should().Contain("supplier");
        emails.Should().OnlyContain(email => !email.Payload.Contains("supplier", StringComparison.OrdinalIgnoreCase));

        // M1 pays from the agency's wallet, so what went back is what the agency paid: never shown to a traveller.
        reversed.Method.Should().NotBe(nameof(RefundMethod.Gateway));
        emails.Single(email => email.TemplateKey == NotificationTemplateCatalog.BookingRefundNotice).Payload.Should()
            .Contain("If you have already paid for it")
            .And.NotContain(DocumentMoney.Format(reversed.AmountMinor, reversed.Currency));
    }

    [Fact]
    public async Task A_traveller_with_no_email_address_still_gets_documents_but_no_email()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);

        // Seeded directly, with a passenger the agent gave no email address.
        var seeded = await harness.SeedConfirmedBookingAsync();

        await FollowUpAsync(harness, followUps => followUps.ConfirmedAsync(new BookingConfirmed(
            harness.AgencyId, seeded.OrderId, seeded.OrderNumber, seeded.OrderLineId, seeded.SupplierBookingId, Pnr: null, harness.Clock.GetUtcNow())));

        (await TravellerEmailsAsync(harness)).Should().BeEmpty();
        (await harness.OutboxAsync<OrderDocumentsRequested>()).Should().ContainSingle()
            .Which.Should().Be(new OrderDocumentsRequested(harness.AgencyId, seeded.OrderId, "Ngozi Adeyemi", RecipientEmail: null));
    }

    /// <summary>One delivery of an event, as the Worker's consumers run it: its own scope, acting as the event's agency.</summary>
    private static async Task FollowUpAsync(BookingPipelineHarness harness, Func<BookingFollowUps, Task> work) =>
        await harness.InAgencyScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<IAppDbContext>();
            var tenant = provider.GetRequiredService<ITenantContext>();
            var platformScope = provider.GetRequiredService<IPlatformScope>();

            // Built by hand: the harness composes the pipeline's own services, and these are not among them.
            var followUps = new BookingFollowUps(
                db,
                new BookingEmails(db, provider.GetRequiredService<INotifier>(), tenant, platformScope),
                new BookingDocuments(db, provider.GetRequiredService<IOutbox>(), tenant, platformScope),
                NullLogger<BookingFollowUps>.Instance);

            await work(followUps);
            return true;
        });

    private static async Task<List<Notification>> TravellerEmailsAsync(BookingPipelineHarness harness)
    {
        await using var db = harness.AsAgency();

        return await db.Notifications
            .Where(notification => TravellerTemplates.Contains(notification.TemplateKey))
            .ToListAsync();
    }
}
