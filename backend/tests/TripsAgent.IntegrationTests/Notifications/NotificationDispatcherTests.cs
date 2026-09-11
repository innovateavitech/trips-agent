using System.Data.Common;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkiaSharp;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
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

    // ------------------------------------------------------------------ the logo and attachments

    [Fact]
    public async Task Traveller_mail_carries_the_agencys_logo_inside_the_message()
    {
        var world = await WorldAsync();
        await world.GiveAgencyALogoAsync();
        var id = await world.QueueAsync(World.BookingConfirmed(world.AgencyA));

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.Sent);

        var email = world.Sent.Should().ContainSingle().Which;
        email.HtmlBody.Should().Contain("src=\"cid:agency-logo\"");

        var logo = email.Attachments.Should().ContainSingle().Which;
        logo.ContentId.Should().Be(NotificationRenderer.InlineLogoContentId);
        logo.ContentType.Should().Be("image/png");
        logo.Content.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task Mail_to_agency_staff_carries_no_agency_logo()
    {
        var world = await WorldAsync();
        await world.GiveAgencyALogoAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:no-logo"));

        await world.DispatchAsync(id);

        var email = world.Sent.Should().ContainSingle().Which;
        (email.Attachments ?? []).Should().BeEmpty();
        email.HtmlBody.Should().NotContain("cid:");
    }

    [Fact]
    public async Task A_notification_sends_the_files_attached_to_it()
    {
        var world = await WorldAsync();
        var pdf = "%PDF-1.7 an invoice"u8.ToArray();
        var asset = await world.StoreGeneratedPdfAsync(world.AgencyA, "INV-2026-000001.pdf", pdf);

        var id = await world.QueueAsync(World.BookingConfirmed(world.AgencyA) with { AttachmentAssetIds = [asset] });

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.Sent);

        var file = world.Sent.Should().ContainSingle().Which.Attachments.Should().ContainSingle().Which;
        file.FileName.Should().Be("INV-2026-000001.pdf");
        file.ContentType.Should().Be(MediaTypes.Pdf);
        file.ContentId.Should().BeNull("it is a file to keep, not part of the body");
        file.Content.Should().Equal(pdf);
    }

    [Fact]
    public async Task Another_agencys_file_is_never_attached()
    {
        var world = await WorldAsync();
        var theirs = await world.StoreGeneratedPdfAsync(world.AgencyB, "INV-2026-000009.pdf", "%PDF-1.7 theirs"u8.ToArray());

        var id = await world.QueueAsync(World.BookingConfirmed(world.AgencyA) with { AttachmentAssetIds = [theirs] });

        (await world.DispatchAsync(id)).Should().Be(NotificationDispatchOutcome.GaveUp);

        world.Sent.Should().BeEmpty();
        (await world.ReadAsync(id)).LastError.Should().Contain("not a servable file belonging to agency");
    }

    // ------------------------------------------------------------------ delivery reports

    [Fact]
    public async Task A_delivery_report_marks_the_notification_delivered_once()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:delivered"));
        await world.DispatchAsync(id);
        var providerId = (await world.ReadAsync(id)).ProviderMessageId!;

        // An offset other than zero, as a provider may send: stored as UTC, never refused.
        var at = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(1));

        (await world.ReportAsync(new DeliveryReport(providerId, DeliveryReportKind.Delivered, null, at)))
            .Should().Be(DeliveryReportOutcome.Recorded);
        (await world.ReportAsync(new DeliveryReport(providerId, DeliveryReportKind.Delivered, null, at)))
            .Should().Be(DeliveryReportOutcome.NothingToChange);

        var stored = await world.ReadAsync(id);
        stored.Status.Should().Be(NotificationStatus.Delivered);
        stored.DeliveredAt.Should().Be(at);
    }

    [Fact]
    public async Task A_bounce_reported_after_sending_marks_it_bounced_and_stops_mail_to_the_address()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:late-bounce"));
        await world.DispatchAsync(id);
        var providerId = (await world.ReadAsync(id)).ProviderMessageId!;

        (await world.ReportAsync(new DeliveryReport(providerId, DeliveryReportKind.Bounced, "550 5.1.1 user unknown", DateTimeOffset.UtcNow)))
            .Should().Be(DeliveryReportOutcome.Recorded);

        (await world.ReadAsync(id)).Status.Should().Be(NotificationStatus.Bounced);

        var next = await world.QueueAsync(world.KybApproved("kyb.approved:after-late-bounce"));
        (await world.DispatchAsync(next)).Should().Be(NotificationDispatchOutcome.Bounced);
        world.Sent.Should().ContainSingle("only the first, which went before the bounce was known");
    }

    [Fact]
    public async Task A_spam_complaint_stops_mail_to_the_address_and_leaves_the_status_alone()
    {
        var world = await WorldAsync();
        var id = await world.QueueAsync(world.KybApproved("kyb.approved:complaint"));
        await world.DispatchAsync(id);
        var providerId = (await world.ReadAsync(id)).ProviderMessageId!;

        (await world.ReportAsync(new DeliveryReport(providerId, DeliveryReportKind.Complained, "abuse report", DateTimeOffset.UtcNow)))
            .Should().Be(DeliveryReportOutcome.Recorded);

        (await world.ReadAsync(id)).Status.Should().Be(NotificationStatus.Sent, "it did arrive");

        await using var db = world.Platform(out _);
        (await db.SuppressedEmailAddresses.SingleAsync()).Reason.Should().Contain("complaint");
    }

    [Fact]
    public async Task A_report_about_a_message_we_never_sent_changes_nothing()
    {
        var world = await WorldAsync();

        (await world.ReportAsync(new DeliveryReport("<unknown@relay>", DeliveryReportKind.Bounced, null, DateTimeOffset.UtcNow)))
            .Should().Be(DeliveryReportOutcome.UnknownMessage);
    }

    // ------------------------------------------------------------------ dedupe under concurrency

    [Fact]
    public async Task Three_runs_at_the_same_moment_queue_one_notification_and_send_one_email()
    {
        var world = await WorldAsync();

        // Three units of work, started together: one commits, the others are refused by the unique
        // index on the dedupe key — and a refused save stages no outbox message either.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => world.TryQueueAsync(world.KybApproved("kyb.approved:race"))));

        List<Guid> ids;
        await using (var db = world.Platform(out var scope))
        {
            using var _ = scope.Enter("test — counting rows");
            ids = await db.Notifications.Select(n => n.Id).ToListAsync();
            (await db.OutboxMessages.CountAsync()).Should().Be(1);
        }

        ids.Should().ContainSingle();

        await world.DispatchAsync(ids[0]);
        world.Sent.Should().ContainSingle();
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

        /// <summary>Queues in its own unit of work; false when a concurrent run's unique key refused it.</summary>
        public async Task<bool> TryQueueAsync(EmailNotificationRequest request)
        {
            try
            {
                await QueueAsync(request);
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // Lost the race after the save: the row it looked up belongs to the winner.
                return false;
            }
        }

        /// <summary>A delivery report from the provider, arriving with no session of its own.</summary>
        public async Task<DeliveryReportOutcome> ReportAsync(DeliveryReport report)
        {
            await using var db = Platform(out var scope);
            return await new NotificationDeliveryReports(db, scope, NullLogger<NotificationDeliveryReports>.Instance)
                .RecordAsync(report);
        }

        /// <summary>Gives agency A a logo that has been scanned clean and processed, as the pipeline would.</summary>
        public async Task GiveAgencyALogoAsync()
        {
            byte[] png;
            using (var bitmap = new SKBitmap(120, 40))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.DarkGreen);
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                png = encoded.ToArray();
            }

            var now = DateTimeOffset.UtcNow;
            var logo = Asset.Reserve(AgencyA, AssetPurpose.AgencyLogo, "logo.png", now.AddMinutes(15));
            var key = $"assets/{AgencyA:N}/{logo.Id:N}/original.png";

            using var content = new MemoryStream(png);
            var stored = await Storage.StoreAsync(content, key, MediaTypes.Png);

            logo.RecordUpload(MediaTypes.Png, stored.SizeBytes);
            logo.TryBeginProcessing(now);
            logo.RecordCleanScan(now);
            logo.ReplaceOriginal(key, MediaTypes.Png, stored.SizeBytes, stored.Checksum);
            logo.MarkReady(120, 40, now);

            await using var db = Platform(out var scope);
            using var _ = scope.Enter("test setup — agency A uploads a logo");

            db.Assets.Add(logo);
            (await db.AgencyBranding.SingleAsync(b => b.AgencyId == AgencyA)).SetLogo(logo.Id);
            await db.SaveChangesAsync();
        }

        /// <summary>Stores a PDF as the document renderer would, and returns its asset id.</summary>
        public async Task<Guid> StoreGeneratedPdfAsync(Guid agencyId, string fileName, byte[] pdf)
        {
            var key = $"documents/{agencyId:N}/{Guid.CreateVersion7():N}/{Guid.CreateVersion7():N}.pdf";

            using var content = new MemoryStream(pdf);
            var stored = await Storage.StoreAsync(content, key, MediaTypes.Pdf);

            var asset = Asset.RecordGenerated(
                agencyId, fileName, key, MediaTypes.Pdf, stored.SizeBytes, stored.Checksum, DateTimeOffset.UtcNow);

            await using var db = Platform(out var scope);
            using var _ = scope.Enter("test setup — a rendered document");

            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            return asset.Id;
        }

        /// <summary>Files on disk for this database alone: logos and attachments.</summary>
        public TripsAgent.Infrastructure.Storage.LocalFileBlobStorage Storage { get; } = new(
            new TripsAgent.Infrastructure.Storage.LocalBlobStorageOptions
            {
                RootPath = Path.Combine(Path.GetTempPath(), "tripsagent-notification-tests", database),
            });

        /// <summary>The dispatcher as the Worker builds it, over <paramref name="db"/>.</summary>
        public NotificationDispatcher Dispatcher(
            AppDbContext db,
            IEmailSender sender,
            TripsAgent.Application.Tenancy.IPlatformScope scope) =>
            new(
                db,
                sender,
                scope,
                new TripsAgent.Infrastructure.Assets.AgencyLogoSource(
                    db, Storage, NullLogger<TripsAgent.Infrastructure.Assets.AgencyLogoSource>.Instance),
                Storage,
                TimeProvider.System,
                NullLogger<NotificationDispatcher>.Instance);

        /// <summary>One consumer delivery: a fresh scope and context, as MassTransit gives each message.</summary>
        public async Task<NotificationDispatchOutcome> DispatchAsync(Guid id, IEmailSender? sender = null)
        {
            await using var db = Platform(out var scope);

            return await Dispatcher(db, sender ?? new CapturingSender(Sent), scope).DispatchAsync(id);
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

            return await Dispatcher(db, sender, tenancy.Scope).DispatchAsync(id);
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
