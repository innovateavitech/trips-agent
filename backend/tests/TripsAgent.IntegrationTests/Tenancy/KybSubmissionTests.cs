using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Tenancy;
using TripsAgent.Application.Tenancy.Kyb;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// KYB upload, submission and status against real PostgreSQL and real blob storage.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KybSubmissionTests : IDisposable
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfake but correctly signed\n");
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly PostgresFixture _postgres;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), $"kyb-tests-{Guid.NewGuid():N}");

    public KybSubmissionTests(PostgresFixture postgres) => _postgres = postgres;

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------- upload

    [Fact]
    public async Task Uploading_a_document_stores_the_bytes_and_records_the_row()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Upload(KybDocumentType.CertificateOfIncorporation, "cert.pdf", PdfBytes);

        var document = outcome.Should().BeOfType<UploadKybDocumentOutcome.Uploaded>().Which.Document;

        document.ContentType.Should().Be(KybDocumentRules.Pdf);
        document.SizeBytes.Should().Be(PdfBytes.Length);
        document.Checksum.Should().MatchRegex("^[0-9a-f]{64}$");

        // The bytes really are in storage, and they are the bytes we sent.
        await using var stored = await world.Storage.OpenReadAsync(document.StorageKey);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(PdfBytes);
    }

    [Fact]
    public async Task The_storage_key_is_generated_not_taken_from_the_filename()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Upload(
            KybDocumentType.TaxIdentification, "../../etc/passwd", PdfBytes);

        var document = ((UploadKybDocumentOutcome.Uploaded)outcome).Document;

        // A key built from user input is how one agency's upload overwrites another's.
        document.StorageKey.Should().NotContain("..");
        document.StorageKey.Should().NotContain("passwd");
        document.StorageKey.Should().StartWith($"kyb/{world.AgencyId:N}/");
        document.FileName.Should().Be("passwd");
    }

    [Fact]
    public async Task A_file_whose_bytes_are_not_an_accepted_format_is_refused()
    {
        await using var world = await WorldAsync();

        // MZ — a Windows executable, named to look like a certificate.
        var outcome = await world.Upload(
            KybDocumentType.CertificateOfIncorporation, "certificate.pdf", [0x4D, 0x5A, 0x90, 0x00]);

        outcome.Should().BeOfType<UploadKybDocumentOutcome.Rejected>()
            .Which.Reason.Should().Contain("contents do not match");
    }

    [Fact]
    public async Task Nothing_is_stored_when_a_file_is_refused()
    {
        await using var world = await WorldAsync();

        await world.Upload(KybDocumentType.CertificateOfIncorporation, "bad.pdf", [0x4D, 0x5A, 0x90, 0x00]);

        // The storage root itself is created when the adapter is constructed, so the question is
        // whether any file was written — not whether the directory exists.
        var written = Directory.Exists(_storageRoot)
            ? Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories)
            : [];

        written.Should().BeEmpty("a refused upload must not leave bytes behind");
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused()
    {
        await using var world = await WorldAsync();

        var oversized = new byte[KybDocumentRules.MaxSizeBytes + 1];
        PdfBytes.CopyTo(oversized, 0);

        var outcome = await world.Upload(KybDocumentType.TaxIdentification, "big.pdf", oversized);

        outcome.Should().BeOfType<UploadKybDocumentOutcome.Rejected>()
            .Which.Reason.Should().Contain("10MB");
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        await using var world = await WorldAsync();

        var outcome = await world.Upload(KybDocumentType.TaxIdentification, "empty.pdf", []);

        // A zero-byte "document" passes every type check and proves nothing.
        outcome.Should().BeOfType<UploadKybDocumentOutcome.Rejected>()
            .Which.Reason.Should().Contain("empty");
    }

    [Fact]
    public async Task Uploading_the_same_document_type_twice_replaces_the_first()
    {
        await using var world = await WorldAsync();

        var first = ((UploadKybDocumentOutcome.Uploaded)await world.Upload(
            KybDocumentType.TaxIdentification, "old.pdf", PdfBytes)).Document;

        var second = ((UploadKybDocumentOutcome.Uploaded)await world.Upload(
            KybDocumentType.TaxIdentification, "new.png", PngBytes)).Document;

        using var _ = world.Tenancy.Scope.Enter("test — counting documents");
        var documents = await world.Db.KybDocuments.ToListAsync();

        // "Re-upload" means replace, which is what the person doing it expects.
        documents.Should().ContainSingle().Which.Id.Should().Be(second.Id);

        // And the superseded bytes are gone, not orphaned in storage.
        (await world.Storage.ExistsAsync(first.StorageKey)).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------- submit

    [Fact]
    public async Task Submitting_moves_the_agency_to_pending_verification_and_raises_an_alert()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();

        var outcome = await world.Submit();

        outcome.Should().BeOfType<SubmitKybOutcome.Submitted>();

        using var _ = world.Tenancy.Scope.Enter("test — reading the agency and the alert queue");

        var agency = await world.Db.Agencies.SingleAsync(a => a.Id == world.AgencyId);
        agency.Status.Should().Be(AgencyStatus.PendingVerification);

        var alert = await world.Db.AdminAlerts.SingleAsync();
        alert.Type.Should().Be(AdminAlertType.PendingKyb);
        alert.Status.Should().Be(AdminAlertStatus.Open);
        alert.AgencyId.Should().Be(world.AgencyId);
        alert.Message.Should().Contain("Lagos Travel");
    }

    [Fact]
    public async Task Submitting_without_the_required_documents_says_which_are_missing()
    {
        await using var world = await WorldAsync();
        await world.Upload(KybDocumentType.ProofOfAddress, "address.pdf", PdfBytes);

        var outcome = await world.Submit();

        outcome.Should().BeOfType<SubmitKybOutcome.Incomplete>()
            .Which.MissingDocumentTypes.Should().Contain("CertificateOfIncorporation");
    }

    [Fact]
    public async Task An_incomplete_submission_raises_no_alert()
    {
        await using var world = await WorldAsync();

        await world.Submit();

        using var _ = world.Tenancy.Scope.Enter("test — counting alerts");

        // Otherwise the review queue fills with agencies that have not actually submitted.
        (await world.Db.AdminAlerts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Documents_are_frozen_once_submitted()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();
        await world.Submit();

        var outcome = await world.Upload(KybDocumentType.ProofOfAddress, "extra.pdf", PdfBytes);

        outcome.Should().BeOfType<UploadKybDocumentOutcome.SubmissionLocked>();
    }

    [Fact]
    public async Task A_rejected_agency_can_re_upload_and_submit_again()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();
        await world.Submit();

        using (var _ = world.Tenancy.Scope.Enter("test — an admin rejects the submission"))
        {
            var submission = await world.Db.KybSubmissions.SingleAsync();
            submission.Reject(Guid.CreateVersion7(), "The certificate is illegible.", world.Clock.GetUtcNow());
            var agency = await world.Db.Agencies.SingleAsync();
            agency.MarkRejected();
            await world.Db.SaveChangesAsync();
        }

        // The whole point of a rejection: the agency fixes the problem and tries again.
        var reupload = await world.Upload(KybDocumentType.CertificateOfIncorporation, "clearer.pdf", PdfBytes);
        reupload.Should().BeOfType<UploadKybDocumentOutcome.Uploaded>();

        (await world.Submit()).Should().BeOfType<SubmitKybOutcome.Submitted>();

        using var scope = world.Tenancy.Scope.Enter("test — confirming the agency is back in review");
        (await world.Db.Agencies.SingleAsync()).Status.Should().Be(AgencyStatus.PendingVerification);
    }

    // ------------------------------------------------------------------------------- status

    [Fact]
    public async Task Status_tells_an_unverified_agency_it_cannot_fund_its_wallet_and_why()
    {
        await using var world = await WorldAsync();

        var status = await world.Status();

        status.CanFundWallet.Should().BeFalse();

        // The FRD asks for the option to be visibly disabled with an explanation, not to fail
        // silently when pressed.
        status.WalletFundingBlockedReason.Should().NotBeNullOrWhiteSpace();
        status.WalletFundingBlockedReason.Should().Contain("KYB");
    }

    [Fact]
    public async Task Status_lists_what_is_still_missing()
    {
        await using var world = await WorldAsync();
        await world.Upload(KybDocumentType.CertificateOfIncorporation, "cert.pdf", PdfBytes);

        var status = await world.Status();

        status.MissingDocumentTypes.Should().Equal("TaxIdentification");
        status.Documents.Should().ContainSingle();
        status.CanEdit.Should().BeTrue();
    }

    [Fact]
    public async Task Status_carries_the_rejection_reason_back_to_the_agency()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();
        await world.Submit();

        using (var _ = world.Tenancy.Scope.Enter("test — an admin rejects the submission"))
        {
            var submission = await world.Db.KybSubmissions.SingleAsync();
            submission.Reject(Guid.CreateVersion7(), "The certificate is illegible.", world.Clock.GetUtcNow());
            await world.Db.SaveChangesAsync();
        }

        var status = await world.Status();

        status.Status.Should().Be(nameof(KybSubmissionStatus.Rejected));
        status.RejectionReason.Should().Be("The certificate is illegible.");
        status.CanEdit.Should().BeTrue("a rejected submission has to be correctable");
    }

    // --------------------------------------------------------------------------- isolation

    [Fact]
    public async Task One_agency_cannot_see_another_agencys_documents()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();

        var otherAgencyId = Guid.CreateVersion7();
        using (var _ = world.Tenancy.Scope.Enter("test — creating a second agency"))
        {
            var other = Agency.RegisterPrincipal("Other Limited", "other-limited", "NG", "NGN", "Africa/Lagos");
            world.Db.Agencies.Add(other);
            await world.Db.SaveChangesAsync();
            otherAgencyId = other.Id;
        }

        await using var asOther = world.ActingAs(otherAgencyId);

        // KYB documents are ITenantScoped, so the global filter should hide them entirely.
        (await asOther.KybDocuments.CountAsync()).Should().Be(0);
        (await asOther.KybSubmissions.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------------- constraints

    [Fact]
    public async Task The_database_refuses_a_disallowed_content_type()
    {
        await using var world = await WorldAsync();
        await world.Upload(KybDocumentType.TaxIdentification, "doc.pdf", PdfBytes);

        // Around the domain, the way a seed script or a hand-written UPDATE would.
        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tenancy.kyb_documents SET content_type = 'image/svg+xml'");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_kyb_documents_content_type");
    }

    [Fact]
    public async Task The_database_refuses_a_document_over_the_limit()
    {
        await using var world = await WorldAsync();
        await world.Upload(KybDocumentType.TaxIdentification, "doc.pdf", PdfBytes);

        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tenancy.kyb_documents SET size_bytes = 10485761");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_kyb_documents_size");
    }

    [Fact]
    public async Task The_database_refuses_a_rejection_with_no_reason()
    {
        await using var world = await WorldAsync();
        await world.UploadRequiredDocuments();
        await world.Submit();

        // The reason is what the agency is shown. A silent refusal is unanswerable.
        var act = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tenancy.kyb_submissions SET status = 'Rejected', reviewed_at = now(), "
            + "reviewed_by_user_id = gen_random_uuid()");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_kyb_submissions_rejection_has_reason");
    }

    [Fact]
    public async Task An_agency_cannot_have_two_open_submissions()
    {
        await using var world = await WorldAsync();
        await world.Upload(KybDocumentType.TaxIdentification, "doc.pdf", PdfBytes);

        var act = async () => await world.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenancy.kyb_submissions (id, agency_id, status, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {world.AgencyId}, 'Draft', now(), now())
             """);

        // Two would make "what is my KYB status?" ambiguous and give the queue two rows to
        // choose between.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ix_kyb_submissions_one_open_per_agency");
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        name = name[..Math.Min(name.Length, 55)];

        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var tenancy = TestTenancy.None();

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            await setup.Database.MigrateAsync();
        }

        Guid agencyId;
        await using (var seed = _postgres.Connect(name, tenancy.Tenant, tenancy.Scope, clock))
        {
            // Inside the platform scope, as every production seeder is: with no tenant resolved,
            // row-level security refuses an unscoped insert (ADR-0006).
            using var _ = tenancy.Scope.Enter("test setup — creating the agency, as a seed script would");

            var agency = Agency.RegisterPrincipal(
                "Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");

            seed.Agencies.Add(agency);
            await seed.SaveChangesAsync();
            agencyId = agency.Id;
        }

        var acting = TestTenancy.For(agencyId);
        var db = _postgres.Connect(name, acting.Tenant, acting.Scope, clock);

        var storage = new LocalFileBlobStorage(new LocalBlobStorageOptions { RootPath = _storageRoot });

        return new World(db, acting, clock, agencyId, storage, name, _postgres);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly UploadKybDocumentHandler _upload;
        private readonly SubmitKybHandler _submit;
        private readonly GetKybStatusHandler _status;
        private readonly string _database;
        private readonly PostgresFixture _postgres;

        public World(
            AppDbContext db,
            (TenantContext Tenant, PlatformScope Scope) tenancy,
            ManualClock clock,
            Guid agencyId,
            IBlobStorage storage,
            string database,
            PostgresFixture postgres)
        {
            Db = db;
            Tenancy = tenancy;
            Clock = clock;
            AgencyId = agencyId;
            Storage = storage;
            _database = database;
            _postgres = postgres;

            _upload = new UploadKybDocumentHandler(db, storage, tenancy.Tenant, clock);
            _submit = new SubmitKybHandler(db, tenancy.Tenant, clock);
            _status = new GetKybStatusHandler(db, tenancy.Tenant);
        }

        public AppDbContext Db { get; }

        public (TenantContext Tenant, PlatformScope Scope) Tenancy { get; }

        public ManualClock Clock { get; }

        public Guid AgencyId { get; }

        public IBlobStorage Storage { get; }

        public Task<UploadKybDocumentOutcome> Upload(KybDocumentType type, string fileName, byte[] content) =>
            _upload.HandleAsync(type, fileName, content.Length, new MemoryStream(content));

        public async Task UploadRequiredDocuments()
        {
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nrequired document\n");

            foreach (var required in KybSubmission.RequiredDocuments)
            {
                await Upload(required, $"{required}.pdf", pdf);
            }
        }

        public Task<SubmitKybOutcome> Submit() => _submit.HandleAsync();

        public Task<Contracts.Tenancy.KybStatusResponse> Status() => _status.HandleAsync();

        /// <summary>A second context acting as a different agency, to prove the filters hold.</summary>
        public AppDbContext ActingAs(Guid agencyId)
        {
            var tenancy = TestTenancy.For(agencyId);
            return _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
