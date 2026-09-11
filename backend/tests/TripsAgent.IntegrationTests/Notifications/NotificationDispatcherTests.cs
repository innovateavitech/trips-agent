using System.Data.Common;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Notifications;

/// <summary>
/// Issue #45, against real PostgreSQL as the policed application role: queue in the caller's
/// transaction, send exactly once, record what happened, retry and give up, bounce and suppress —
/// and never put our brand in front of a traveller.
/// </summary>
/// <remarks>
/// The dispatcher is called directly, as <c>NotificationQueuedConsumer</c> would call it for each
/// message. What the broker adds — redelivery with backoff, the <c>_error</c> queue — is
/// MassTransit's own tested behaviour; what is tested here is that the row tells the same story.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class NotificationDispatcherTests
{
    private const string AgencyColor = "#0A7E3B";

    private readonly PostgresFixture _postgres;

    public NotificationDispatcherTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ queue and send

    [Fact]
    public async Task Queueing_writes_the_row_and_its_outbox_message_in_the_callers_save()
    {
        var world = await WorldAsync();

        var id = await world.QueueAsync(world.KybApproved("kyb.approved:1"));

        await using var db = world.Platform(out var scope);
        using var _ = scope.Enter("test — reading the queue");

        (await db.Notifications.SingleAsync(n => n.Id == id)).Status.Should().Be(NotificationStatus.Queued);

        var message = await db.OutboxMessages.SingleAsync();
        message.MessageType.Should().Contain(nameof(NotificationQueued));
        message.Payload.Should().Contain(id.ToString());
    }

    [Fact]
    public async Task A_queued_notification_is_sent_and_its_delivery_recorded()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:1"));

        var outcome = await world.DispatchAsync(id);

        outcome.Should().Be(NotificationDispatchOutcome.Sent);
        world.Sent.Should().ContainSingle().Which.To.Should().Be(world.OwnerEmail);

        var stored = await world.ReadAsync(id);
        stored.Status.Should().Be(NotificationStatus.Sent);
        stored.Attempts.Should().Be(1);
        stored.TemplateVersion.Should().Be(1);
        stored.ProviderMessageId.Should().NotBeNullOrEmpty();
        stored.SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_job_that_runs_three_times_sends_exactly_one_email()
    {
        var world = await WorldAsync();

        // Three runs, three separate units of work, the same thing to say.
        var first = await world.QueueAsync(world.KybApproved("kyb.approved:triple"));
        await world.QueueAsync(world.KybApproved("kyb.approved:triple"));
        await world.QueueAsync(world.KybApproved("kyb.approved:triple"));

        // And the broker delivering the one message three times, which at-least-once allows.
        await world.DispatchAsync(first);
        await world.DispatchAsync(first);
        await world.DispatchAsync(first);

        world.Sent.Should().ContainSingle();

        await using var db = world.Platform(out var scope);
        using var _ = scope.Enter("test — counting rows");
        (await db.Notifications.CountAsync()).Should().Be(1);
        (await db.OutboxMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_same_dedupe_key_for_two_agencies_is_two_notifications()
    {
        var world = await WorldAsync();

        await world.QueueAsync(world.KybApproved("shared-key"));
        await world.QueueAsync(world.KybApproved("shared-key") with { AgencyId = world.AgencyB });

        await using var db = world.Platform(out var scope);
        using var _ = scope.Enter("test — counting rows");
        (await db.Notifications.CountAsync()).Should().Be(2);
    }

    // ------------------------------------------------------------------ failure

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_fifth_gives_up()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:flaky"));
        var relayDown = new FailingSender(new IOException("relay unreachable"));

        for (var attempt = 1; attempt < NotificationDispatcher.MaxAttempts; attempt++)
        {
            (await world.DispatchAsync(id, relayDown)).Should().Be(NotificationDispatchOutcome.RetryLater);
        }

        var beforeLast = await world.ReadAsync(id);
        beforeLast.Status.Should().Be(NotificationStatus.Queued);
        beforeLast.Attempts.Should().Be(NotificationDispatcher.MaxAttempts - 1);
        beforeLast.LastError.Should().Contain("relay unreachable");

        (await world.DispatchAsync(id, relayDown)).Should().Be(NotificationDispatchOutcome.GaveUp);

        var dead = await world.ReadAsync(id);
        dead.Status.Should().Be(NotificationStatus.Failed);
        dead.Attempts.Should().Be(NotificationDispatcher.MaxAttempts);

        // Dead is dead: a redelivery from the broker's retry does nothing.
        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.Skipped);
        world.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_that_fails_after_the_relay_accepted_does_not_send_it_again()
    {
        // The reviewer's case: PostgreSQL still answers reads but refuses writes with an error
        // Npgsql calls transient (53100 disk_full), starting the moment the relay has the email.
        // EF's retrying strategy replays the failed save, and the broker redelivers the message.
        // Neither may turn one KYB decision into twenty identical emails.
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:db-down-after-send"));

        var outage = new NotificationWritesRefused();
        var sender = new CapturingSender(world.Sent, afterSend: () => outage.Armed = true);

        // The first delivery sends, then cannot record it and throws, so the broker will retry.
        await FluentActions.Awaiting(() => world.DispatchAsync(id, sender, outage))
            .Should().ThrowAsync<Exception>();

        outage.Refused.Should().BeGreaterThan(1, "the retrying strategy really did replay the failed save");

        // The broker's four retries find the attempt already claimed and leave it alone.
        for (var retry = 1; retry < NotificationDispatcher.MaxAttempts; retry++)
        {
            (await world.DispatchAsync(id, sender, outage)).Should().Be(NotificationDispatchOutcome.Skipped);
        }

        world.Sent.Should().ContainSingle("the relay accepted it once, and nothing may resend an unknown outcome");

        // Left for a person, with the attempt counted: 'sending' is "handed over, result unknown".
        var stored = await world.ReadAsync(id);
        stored.Status.Should().Be(NotificationStatus.Sending);
        stored.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_permanent_rejection_bounces_suppresses_the_address_and_is_not_retried()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:bounce"));

        var outcome = await world.DispatchAsync(
            id, new FailingSender(new EmailRejectedException("550 5.1.1 no such user", addressIsUndeliverable: true)));

        outcome.Should().Be(NotificationDispatchOutcome.Bounced);
        (await world.ReadAsync(id)).Status.Should().Be(NotificationStatus.Bounced);

        await using var db = world.Platform(out var scope);
        (await db.SuppressedEmailAddresses.SingleAsync()).Address.Should().Be(world.OwnerEmail);

        // The next mail to that address is held back without troubling the relay at all.
        var next = await world.QueueAsync(world.KybApproved("kyb.approved:after-bounce"));

        (await world.DispatchAsync(next)).Should().Be(NotificationDispatchOutcome.Bounced);
        world.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_about_our_own_setup_fails_the_notification_but_does_not_suppress_the_address()
    {
        // What every recipient gets while the relay credentials are wrong: a permanent refusal that
        // says nothing about the mailbox. Suppressing on it would silence a working inbox for every
        // agency until someone edited the table by hand.
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:relay-denied"));
        var relayDenied = new EmailRejectedException("The relay refused it: 550 5.7.1 Relaying denied", addressIsUndeliverable: false);

        (await world.DispatchAsync(id, new FailingSender(relayDenied))).Should().Be(NotificationDispatchOutcome.GaveUp);

        var stored = await world.ReadAsync(id);
        stored.Status.Should().Be(NotificationStatus.Failed, "retrying into a permanent refusal only hurts our reputation");
        stored.Attempts.Should().Be(1);
        stored.LastError.Should().Contain("5.7.1");

        await using (var db = world.Platform(out _))
        {
            (await db.SuppressedEmailAddresses.CountAsync()).Should().Be(0);
        }

        // Once the relay is fixed, the same address gets its mail.
        var next = await world.QueueAsync(world.KybApproved("kyb.approved:after-relay-fixed"));

        (await world.DispatchAsync(next)).Should().Be(NotificationDispatchOutcome.Sent);
        world.Sent.Should().ContainSingle().Which.To.Should().Be(world.OwnerEmail);
    }

    [Fact]
    public async Task A_notification_that_cannot_render_is_failed_at_once_rather_than_retried()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:no-template"));

        await using (var db = world.Platform(out var scope))
        {
            // As if this build's templates had never been seeded.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM notifications.notification_templates");
        }

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.GaveUp);

        var stored = await world.ReadAsync(id);
        stored.Attempts.Should().Be(1);
        stored.LastError.Should().Contain("migrate");
    }

    // ------------------------------------------------------------------ the brand rule

    [Fact]
    public async Task Traveller_mail_goes_out_under_the_agencys_brand_and_never_ours()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(World.BookingConfirmed(world.AgencyA));

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.Sent);

        var email = world.Sent.Should().ContainSingle().Which;
        email.FromName.Should().Be("Lagos Travel");
        email.ReplyTo.Should().Be(world.OwnerEmail, "a traveller who replies must reach their agent");
        email.HtmlBody.Should().Contain(AgencyColor).And.Contain("Lagos Travel");

        foreach (var part in new[] { email.Subject, email.HtmlBody, email.TextBody, email.FromName! })
        {
            part.Should().NotContainEquivalentOf(NotificationTemplateCatalog.ProductName);
            part.Should().NotContainEquivalentOf("tripsagent");
        }
    }

    [Fact]
    public async Task Traveller_mail_for_an_agency_that_would_show_our_brand_is_refused()
    {
        var world = await WorldAsync(agencyTradingName: "Trips Agent Travels");
        var id = await world.QueueAsync(World.BookingConfirmed(world.AgencyA));

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.GaveUp);

        world.Sent.Should().BeEmpty();
        (await world.ReadAsync(id)).LastError.Should().Contain("rule 4");
    }

    // ------------------------------------------------------------------ tenancy

    [Fact]
    public async Task An_agency_sees_its_own_notifications_and_no_one_elses_even_without_the_filter()
    {
        var world = await WorldAsync();
        await world.QueueAsync(world.KybApproved("kyb.approved:a"));

        await using var asB = world.ActingAs(world.AgencyB);
        await using var asA = world.ActingAs(world.AgencyA);

        // Row-level security, not the EF filter, is what answers here.
        (await asB.Notifications.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await asA.Notifications.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync(
        string agencyTradingName = "Lagos Travel",
        [CallerMemberName] string testName = "")
    {
        var name = $"ntf_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();
        await NotificationTemplateSeeder.EnsureAsync(setup, TimeProvider.System);

        using var _ = tenancy.Scope.Enter("test setup — two agencies, one with branding and an owner");

        var a = Agency.RegisterPrincipal(
            "Lagos Travel Services Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos", tradingName: agencyTradingName);
        var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

        var branding = AgencyBranding.CreateDefault(a);
        branding.SetColors(AgencyColor, null);
        branding.SetContactAddress("12 Marina, Lagos");

        var owner = User.ForAgency(a.Id, "owner@lagos-travel.test", "argon2id$hash", "Ngozi", "Adeyemi");

        setup.Agencies.AddRange(a, b);
        setup.AgencyBranding.Add(branding);
        setup.Users.Add(owner);
        await setup.SaveChangesAsync();

        return new World(_postgres, name, a.Id, b.Id, owner.Email);
    }

    private sealed class World(PostgresFixture postgres, string database, Guid agencyA, Guid agencyB, string ownerEmail)
    {
        public Guid AgencyA { get; } = agencyA;

        public Guid AgencyB { get; } = agencyB;

        public string OwnerEmail { get; } = ownerEmail;

        public List<EmailMessage> Sent { get; } = [];

        public EmailNotificationRequest KybApproved(string dedupeKey) =>
            new(
                AgencyA,
                NotificationTemplateCatalog.KybApproved,
                OwnerEmail,
                "Ngozi",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["businessName"] = "Lagos Travel" },
                dedupeKey);

        public static EmailNotificationRequest BookingConfirmed(Guid agencyId) =>
            new(
                agencyId,
                NotificationTemplateCatalog.BookingConfirmed,
                "ada.obi@example.test",
                "Ada",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = "LT-1001",
                    ["itinerarySummary"] = "Lagos (LOS) to Abuja (ABV)",
                    ["travellerNames"] = "Ada Obi",
                    ["departureDate"] = "3 October 2026",
                },
                "booking.confirmed:LT-1001");

        /// <summary>Queues one notification in its own unit of work, as a job or a handler would.</summary>
        public async Task<Guid> QueueAsync(EmailNotificationRequest request)
        {
            await using var db = Platform(out var scope);
            using var _ = scope.Enter("test — queueing as a platform job would");

            await new Notifier(db, new EfOutbox(db, TimeProvider.System)).QueueEmailAsync(request);
            await db.SaveChangesAsync();

            return await db.Notifications
                .Where(n => n.AgencyId == request.AgencyId && n.DedupeKey == request.DedupeKey)
                .Select(n => n.Id)
                .SingleAsync();
        }

        /// <summary>One consumer delivery: a fresh scope and context, as MassTransit gives each message.</summary>
        public async Task<NotificationDispatchOutcome> DispatchAsync(Guid id, IEmailSender? sender = null)
        {
            await using var db = Platform(out var scope);

            var dispatcher = new NotificationDispatcher(
                db, sender ?? new CapturingSender(Sent), scope, TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);

            return await dispatcher.DispatchAsync(id);
        }

        /// <summary>
        /// One consumer delivery on a context configured as production configures it — EF's
        /// retry-on-transient-failure on — with <paramref name="interceptor"/> between it and the database.
        /// </summary>
        public async Task<NotificationDispatchOutcome> DispatchAsync(Guid id, IEmailSender sender, IInterceptor interceptor)
        {
            var tenancy = TestTenancy.None();
            await using var db = postgres.Connect(
                database, tenancy.Tenant, tenancy.Scope, retryOnFailure: true, interceptors: [interceptor]);

            var dispatcher = new NotificationDispatcher(
                db, sender, tenancy.Scope, TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);

            return await dispatcher.DispatchAsync(id);
        }

        public async Task<Notification> ReadAsync(Guid id)
        {
            await using var db = Platform(out var scope);
            using var _ = scope.Enter("test — reading the outcome");

            return await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id);
        }

        /// <summary>A context with no tenant — the Worker's view — as the policed role.</summary>
        public AppDbContext Platform(out TripsAgent.Infrastructure.Tenancy.PlatformScope scope)
        {
            var tenancy = TestTenancy.None();
            scope = tenancy.Scope;
            return postgres.Connect(database, tenancy.Tenant, tenancy.Scope);
        }

        public AppDbContext ActingAs(Guid agencyId)
        {
            var tenancy = TestTenancy.For(agencyId);
            return postgres.Connect(database, tenancy.Tenant, tenancy.Scope);
        }
    }

    private sealed class CapturingSender(List<EmailMessage> sent, Action? afterSend = null) : IEmailSender
    {
        public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            sent.Add(message);
            afterSend?.Invoke();
            return Task.FromResult(new EmailReceipt($"<{Guid.NewGuid():N}@test>"));
        }
    }

    /// <summary>
    /// Once armed, refuses every write to a notification row with disk_full — an error Npgsql
    /// reports as transient, so EF's retrying strategy replays whatever it was running.
    /// </summary>
    private sealed class NotificationWritesRefused : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Refused { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            RefuseIfArmed(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            RefuseIfArmed(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void RefuseIfArmed(DbCommand command)
        {
            // "UPDATE notifications.notifications SET", so the claim's SELECT ... FOR UPDATE still runs.
            if (Armed && command.CommandText.Contains("UPDATE notifications.notifications SET", StringComparison.OrdinalIgnoreCase))
            {
                Refused++;
                throw new PostgresException(
                    "could not extend file: No space left on device", "ERROR", "ERROR", PostgresErrorCodes.DiskFull);
            }
        }
    }

    private sealed class FailingSender(Exception failure) : IEmailSender
    {
        public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromException<EmailReceipt>(failure);
    }
}
