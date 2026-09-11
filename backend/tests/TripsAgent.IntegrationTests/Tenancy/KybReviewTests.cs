using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Tenancy;
using TripsAgent.Application.Tenancy.Kyb;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// The Trips-side review: the queue, the decision, the notification and the audit trail.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KybReviewTests : IDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nkyb document\n");
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 17)).ToArray();

    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"kyb-review-{Guid.NewGuid():N}");

    public KybReviewTests(PostgresFixture postgres) => _postgres = postgres;

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    // --------------------------------------------------------------------------------- queue

    [Fact]
    public async Task The_queue_lists_submitted_agencies_oldest_first()
    {
        await using var world = await WorldAsync();

        var first = await world.SubmitAgency("Alpha Limited", "alpha", submittedAt: world.Clock.GetUtcNow());
        world.Clock.Advance(TimeSpan.FromHours(2));
        var second = await world.SubmitAgency("Beta Limited", "beta", submittedAt: world.Clock.GetUtcNow());

        var queue = await world.Review.QueueAsync();

        // An agency that cannot transact loses business every day it waits, so the queue is a
        // FIFO and not a stack.
        queue.Select(q => q.SubmissionId).Should().Equal(first.SubmissionId, second.SubmissionId);
        queue[0].AgencyName.Should().Be("Alpha Limited");
        queue[0].DocumentCount.Should().Be(KybSubmission.RequiredDocuments.Count);
    }

    [Fact]
    public async Task A_draft_submission_is_not_in_the_queue()
    {
        await using var world = await WorldAsync();

        // Uploaded but never submitted: the agency is still working on it.
        await world.CreateAgencyWithDraft("Draft Limited", "draft-limited");

        (await world.Review.QueueAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_decided_submission_leaves_the_queue()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Gamma Limited", "gamma", world.Clock.GetUtcNow());

        await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        (await world.Review.QueueAsync()).Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------ decisions

    [Fact]
    public async Task Approving_verifies_the_agency_and_emails_it()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Delta Limited", "delta", world.Clock.GetUtcNow());

        var outcome = await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        outcome.Should().BeOfType<KybDecisionOutcome.Decided>();

        using var _ = world.Tenancy.Scope.Enter("test — reading the decided agency");
        var agency = await world.Db.Agencies.SingleAsync(a => a.Id == submitted.AgencyId);

        agency.Status.Should().Be(AgencyStatus.Verified);
        agency.VerifiedAt.Should().NotBeNull();

        var email = world.Sent.Should().ContainSingle().Which;
        email.To.Should().Be(submitted.OwnerEmail);
        email.Subject.Should().Contain("verified");
    }

    [Fact]
    public async Task Approving_unblocks_wallet_funding()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Epsilon Limited", "epsilon", world.Clock.GetUtcNow());

        await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        using var _ = world.Tenancy.Scope.Enter("test — checking the funding rule");
        var agency = await world.Db.Agencies.SingleAsync(a => a.Id == submitted.AgencyId);

        // The whole point of verification, from the agency's side.
        WalletFundingPolicy.For(agency).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Rejecting_requires_a_reason()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Zeta Limited", "zeta", world.Clock.GetUtcNow());

        var outcome = await world.Review.RejectAsync(submitted.SubmissionId, world.ReviewerId, "   ");

        outcome.Should().BeOfType<KybDecisionOutcome.ReasonRequired>();

        using var _ = world.Tenancy.Scope.Enter("test — nothing should have changed");
        (await world.Db.Agencies.SingleAsync(a => a.Id == submitted.AgencyId))
            .Status.Should().Be(AgencyStatus.PendingVerification);

        world.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejecting_emails_the_agency_the_reason_verbatim()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Eta Limited", "eta", world.Clock.GetUtcNow());

        const string reason = "The certificate of incorporation is illegible — send a clearer scan.";

        await world.Review.RejectAsync(submitted.SubmissionId, world.ReviewerId, reason);

        var email = world.Sent.Should().ContainSingle().Which;
        email.To.Should().Be(submitted.OwnerEmail);
        email.TextBody.Should().Contain(reason);

        using var _ = world.Tenancy.Scope.Enter("test — reading the decision back");
        var submission = await world.Db.KybSubmissions.SingleAsync(s => s.Id == submitted.SubmissionId);

        submission.Status.Should().Be(KybSubmissionStatus.Rejected);
        submission.RejectionReason.Should().Be(reason);
        submission.ReviewedByUserId.Should().Be(world.ReviewerId);
    }

    [Fact]
    public async Task A_rejection_reason_containing_markup_is_escaped_in_the_email()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Theta Limited", "theta", world.Clock.GetUtcNow());

        await world.Review.RejectAsync(
            submitted.SubmissionId, world.ReviewerId, "<a href=\"https://evil.example\">click</a>");

        // An admin typed this. It must arrive as text, not as a working link in an email that
        // looks like it came from us.
        var email = world.Sent.Single();
        email.HtmlBody.Should().NotContain("<a href=\"https://evil.example\"");
        email.HtmlBody.Should().Contain("&lt;a href=");
    }

    [Fact]
    public async Task A_submission_cannot_be_decided_twice()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Iota Limited", "iota", world.Clock.GetUtcNow());

        await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        // Two admins opening the queue at once must not both be able to decide.
        var second = await world.Review.RejectAsync(submitted.SubmissionId, world.ReviewerId, "changed my mind");

        second.Should().BeOfType<KybDecisionOutcome.NotAwaitingDecision>();
    }

    [Fact]
    public async Task Deciding_closes_the_alert()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Kappa Limited", "kappa", world.Clock.GetUtcNow());

        await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        using var _ = world.Tenancy.Scope.Enter("test — reading the alert queue");
        var alert = await world.Db.AdminAlerts.SingleAsync(a => a.EntityId == submitted.SubmissionId);

        alert.Status.Should().Be(AdminAlertStatus.Resolved);
    }

    // -------------------------------------------------------------------------- audit trail

    [Fact]
    public async Task A_decision_is_written_to_the_audit_log_with_the_actor_and_before_and_after()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Lambda Limited", "lambda", world.Clock.GetUtcNow());

        world.Audit.ActorUserId = world.ReviewerId;
        world.Audit.ActorType = AuditActorType.User;

        await world.Review.RejectAsync(submitted.SubmissionId, world.ReviewerId, "Documents do not match the business name.");

        using var _ = world.Tenancy.Scope.Enter("test — reading the audit log");

        // Two rows exist for this submission — it was created, then decided. The decision is the
        // update, and that is the one an auditor is asking about.
        var entries = await world.Db.AuditLogs
            .Where(e => e.EntityType == nameof(KybSubmission)
                        && e.EntityId == submitted.SubmissionId.ToString())
            .ToListAsync();

        var decision = entries.Should()
            .ContainSingle(e => e.Action == AuditActions.Updated).Subject;

        decision.ActorUserId.Should().Be(world.ReviewerId);
        decision.Reason.Should().Be("Documents do not match the business name.");

        // Before and after state is the point: "who changed this, from what, to what".
        decision.BeforeState.Should().Contain("Submitted");
        decision.AfterState.Should().Contain("Rejected");
    }

    [Fact]
    public async Task The_agencys_status_change_is_audited_too()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Mu Limited", "mu", world.Clock.GetUtcNow());

        world.Audit.ActorUserId = world.ReviewerId;
        world.Audit.ActorType = AuditActorType.User;

        await world.Review.ApproveAsync(submitted.SubmissionId, world.ReviewerId);

        using var _ = world.Tenancy.Scope.Enter("test — reading the audit log");

        var agencyEntry = await world.Db.AuditLogs
            .Where(e => e.EntityType == nameof(Agency) && e.EntityId == submitted.AgencyId.ToString())
            .ToListAsync();

        agencyEntry.Should().NotBeEmpty("an agency becoming verified is exactly what an auditor asks about");
        agencyEntry.Should().Contain(e => e.AfterState != null && e.AfterState.Contains("Verified"));
    }

    // ------------------------------------------------------------------------- document links

    [Fact]
    public async Task Documents_come_back_behind_a_link_that_expires()
    {
        await using var world = await WorldAsync();
        var submitted = await world.SubmitAgency("Nu Limited", "nu", world.Clock.GetUtcNow());

        var detail = await world.Review.DetailAsync(submitted.SubmissionId);

        detail.Should().NotBeNull();
        detail!.Documents.Should().HaveCount(KybSubmission.RequiredDocuments.Count);

        var document = detail.Documents[0];
        document.Url.Should().Contain("signature=");
        document.UrlExpiresAt.Should().Be(world.Clock.GetUtcNow().Add(KybDocumentLink.Lifetime));
    }

    [Fact]
    public async Task A_valid_link_is_accepted_and_an_expired_one_is_not()
    {
        await using var world = await WorldAsync();
        var documentId = Guid.CreateVersion7();

        var link = world.Links.Create(documentId);
        var (expires, signature) = ParseLink(link.Path);

        world.Links.IsValid(documentId, expires, signature).Should().BeTrue();

        world.Clock.Advance(KybDocumentLink.Lifetime + TimeSpan.FromSeconds(1));

        world.Links.IsValid(documentId, expires, signature).Should().BeFalse();
    }

    [Fact]
    public async Task A_link_for_one_document_does_not_open_another()
    {
        await using var world = await WorldAsync();
        var documentId = Guid.CreateVersion7();

        var (expires, signature) = ParseLink(world.Links.Create(documentId).Path);

        // The signature is scoped to a document id, so swapping the id in the URL fails.
        world.Links.IsValid(Guid.CreateVersion7(), expires, signature).Should().BeFalse();
    }

    [Fact]
    public async Task Moving_the_deadline_invalidates_the_signature()
    {
        await using var world = await WorldAsync();
        var documentId = Guid.CreateVersion7();

        var (expires, signature) = ParseLink(world.Links.Create(documentId).Path);

        // The expiry is inside the signed payload, so editing it in the URL does not extend it.
        world.Links.IsValid(documentId, expires + 3600, signature).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-signature")]
    public async Task A_missing_or_forged_signature_is_refused(string signature)
    {
        await using var world = await WorldAsync();
        var documentId = Guid.CreateVersion7();
        var (expires, _) = ParseLink(world.Links.Create(documentId).Path);

        world.Links.IsValid(documentId, expires, signature).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------ helpers

    private static (long Expires, string Signature) ParseLink(string path)
    {
        var query = System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..]);

        return (long.Parse(query["expires"]!, System.Globalization.CultureInfo.InvariantCulture),
                query["signature"]!);
    }

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        // Starts at the real current time, not a fixed date: audit_logs is partitioned by month
        // and the migration creates partitions around today, so a clock parked in another month
        // has nowhere to write. Nothing here depends on the absolute date — the tests move the
        // clock relatively.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            await setup.Database.MigrateAsync();
            await ReferenceDataSeeder.EnsureAsync(setup, tenancy.Scope);
        }

        var audit = new AuditContext();
        var db = _postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock, audit);

        return new World(db, tenancy, clock, audit, _storageRoot, name, _postgres);
    }

    private sealed record SubmittedAgency(Guid AgencyId, Guid SubmissionId, string OwnerEmail);

    private sealed class World : IAsyncDisposable
    {
        private readonly LocalFileBlobStorage _storage;
        private readonly string _database;
        private readonly PostgresFixture _postgres;

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            AuditContext audit,
            string storageRoot,
            string database,
            PostgresFixture postgres)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            Audit = audit;
            _database = database;
            _postgres = postgres;

            _storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = storageRoot });
            Links = new KybDocumentLink(new HmacTokenHasher(HashKey), clock);

            Review = new KybReviewHandler(
                db,
                tenancy.Scope,
                audit,
                new CapturingEmailSender(Sent),
                Links,
                clock,
                NullLogger<KybReviewHandler>.Instance);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public AuditContext Audit { get; }

        public KybDocumentLink Links { get; }

        public KybReviewHandler Review { get; }

        public List<EmailMessage> Sent { get; } = [];

        public Guid ReviewerId { get; } = Guid.CreateVersion7();

        /// <summary>An agency with its required documents uploaded and submitted for review.</summary>
        public async Task<SubmittedAgency> SubmitAgency(string legalName, string slug, DateTimeOffset submittedAt)
        {
            var (agencyId, submissionId, email) = await CreateAgencyWithDraft(legalName, slug);

            await using var asAgency = ActingAs(agencyId);
            var submission = await asAgency.KybSubmissions.SingleAsync(s => s.Id == submissionId);
            var agency = await asAgency.Agencies.SingleAsync(a => a.Id == agencyId);

            submission.Submit(KybSubmission.RequiredDocuments, submittedAt);
            agency.MarkPendingVerification();
            asAgency.AdminAlerts.Add(AdminAlert.ForPendingKyb(agencyId, submissionId, legalName));
            await asAgency.SaveChangesAsync();

            return new SubmittedAgency(agencyId, submissionId, email);
        }

        /// <summary>An agency with an open draft submission and its documents uploaded.</summary>
        public async Task<(Guid AgencyId, Guid SubmissionId, string OwnerEmail)> CreateAgencyWithDraft(
            string legalName, string slug)
        {
            Guid agencyId;
            Guid submissionId;
            var email = $"owner@{slug}.test";

            using (var _ = Tenancy.Scope.Enter("test setup — creating an agency and its documents"))
            {
                var agency = Agency.RegisterPrincipal(legalName, slug, "NG", "NGN", "Africa/Lagos");
                Db.Agencies.Add(agency);

                var owner = User.ForAgency(agency.Id, email, "argon2id$hash", "Owner", legalName);
                Db.Users.Add(owner);

                var submission = KybSubmission.StartFor(agency.Id);
                Db.KybSubmissions.Add(submission);
                await Db.SaveChangesAsync();

                foreach (var required in KybSubmission.RequiredDocuments)
                {
                    var key = $"kyb/{agency.Id:N}/{submission.Id:N}/{Guid.CreateVersion7():N}";
                    var stored = await _storage.StoreAsync(new MemoryStream(Pdf), key, KybDocumentRules.Pdf);

                    Db.KybDocuments.Add(KybDocument.Create(
                        agency.Id, submission.Id, required, $"{required}.pdf",
                        key, KybDocumentRules.Pdf, stored.SizeBytes, stored.Checksum));
                }

                await Db.SaveChangesAsync();

                agencyId = agency.Id;
                submissionId = submission.Id;
            }

            Db.ChangeTracker.Clear();
            return (agencyId, submissionId, email);
        }

        public AppDbContext ActingAs(Guid agencyId)
        {
            var tenancy = TestTenancy.For(agencyId);
            return _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class CapturingEmailSender(List<EmailMessage> sent) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
