using FluentAssertions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.UnitTests.Tenancy;

public class FileSignatureTests
{
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void A_pdf_is_recognised() => FileSignature.Detect(Pdf).Should().Be(KybDocumentRules.Pdf);

    [Fact]
    public void A_jpeg_is_recognised() => FileSignature.Detect(Jpeg).Should().Be(KybDocumentRules.Jpeg);

    [Fact]
    public void A_png_is_recognised() => FileSignature.Detect(Png).Should().Be(KybDocumentRules.Png);

    [Fact]
    public void An_executable_renamed_to_pdf_is_not_recognised()
    {
        // MZ — a Windows executable. The filename and the browser's content type are both set by
        // whoever is uploading; the first bytes are set by whatever wrote the file.
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

        FileSignature.Detect(executable).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]                    // zip
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]        // gif
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]                    // svg — scriptable
    [InlineData(new byte[] { })]                                           // empty
    public void Anything_else_is_refused(byte[] leadingBytes) =>
        FileSignature.Detect(leadingBytes).Should().BeNull();

    [Fact]
    public void A_webp_is_recognised()
    {
        // RIFF, four bytes of length, then WEBP at offset 8.
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

        FileSignature.Detect(webp).Should().Be(MediaTypes.Webp);
    }

    [Fact]
    public void A_wav_is_not_mistaken_for_a_webp()
    {
        // Same RIFF container, different form type. The prefix alone would have accepted it.
        byte[] wav = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];

        FileSignature.Detect(wav).Should().BeNull();
    }

    [Fact]
    public void A_webp_is_recognised_but_is_not_a_kyb_document()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

        // Recognising a format and accepting it are two different decisions.
        KybDocumentRules.IsAllowedContentType(FileSignature.Detect(webp)).Should().BeFalse();
    }

    [Fact]
    public void A_truncated_png_signature_is_refused()
    {
        // The full eight-byte signature is required, so a file that merely starts like a PNG is
        // not mistaken for one.
        FileSignature.Detect(Png.AsSpan(0, 4)).Should().BeNull();
    }
}

public class KybDocumentRulesTests
{
    [Fact]
    public void The_size_limit_is_ten_megabytes()
    {
        KybDocumentRules.MaxSizeBytes.Should().Be(10 * 1024 * 1024);
        KybDocumentRules.MaxSizeDescription.Should().Be("10MB");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10 * 1024 * 1024, true)]
    [InlineData(10 * 1024 * 1024 + 1, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Size_is_bounded_at_both_ends(long size, bool allowed) =>
        KybDocumentRules.IsAllowedSize(size).Should().Be(allowed);

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("APPLICATION/PDF", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void Only_pdf_jpeg_and_png_are_accepted(string? contentType, bool allowed) =>
        KybDocumentRules.IsAllowedContentType(contentType).Should().Be(allowed);
}

public class KybSubmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly KybDocumentType[] Complete =
    [
        KybDocumentType.CertificateOfIncorporation,
        KybDocumentType.TaxIdentification,
    ];

    [Fact]
    public void A_new_submission_is_an_editable_draft()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());

        submission.Status.Should().Be(KybSubmissionStatus.Draft);
        submission.IsEditable.Should().BeTrue();
        submission.IsAwaitingDecision.Should().BeFalse();
    }

    [Fact]
    public void Submitting_freezes_the_documents()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());

        submission.Submit(Complete, Now);

        submission.Status.Should().Be(KybSubmissionStatus.Submitted);
        submission.SubmittedAt.Should().Be(Now);
        submission.IsEditable.Should().BeFalse("documents are frozen once they are with Trips");
    }

    [Fact]
    public void Submitting_without_the_required_documents_is_refused()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());

        var act = () => submission.Submit([KybDocumentType.ProofOfAddress], Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*CertificateOfIncorporation*");
    }

    [Fact]
    public void A_rejection_must_say_why()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());
        submission.Submit(Complete, Now);

        var act = () => submission.Reject(Guid.CreateVersion7(), "   ", Now);

        // "Rejected" with no explanation is an unanswerable support ticket.
        act.Should().Throw<ArgumentException>().WithMessage("*must say why*");
    }

    [Fact]
    public void A_rejected_submission_becomes_editable_again()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());
        submission.Submit(Complete, Now);
        submission.Reject(Guid.CreateVersion7(), "The certificate is illegible.", Now);

        submission.Status.Should().Be(KybSubmissionStatus.Rejected);
        submission.RejectionReason.Should().Be("The certificate is illegible.");
        submission.IsEditable.Should().BeTrue("the agency has to be able to correct and resubmit");
    }

    [Fact]
    public void Resubmitting_clears_the_previous_refusal()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());
        submission.Submit(Complete, Now);
        submission.Reject(Guid.CreateVersion7(), "The certificate is illegible.", Now);

        submission.Submit(Complete, Now.AddDays(1));

        // Otherwise the agency still sees last week's refusal while Trips looks at the fix.
        submission.RejectionReason.Should().BeNull();
        submission.ReviewedAt.Should().BeNull();
        submission.Status.Should().Be(KybSubmissionStatus.Submitted);
    }

    [Fact]
    public void A_submission_already_with_Trips_cannot_be_submitted_again()
    {
        var submission = KybSubmission.StartFor(Guid.CreateVersion7());
        submission.Submit(Complete, Now);

        var act = () => submission.Submit(Complete, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot be submitted again*");
    }

    [Fact]
    public void Only_a_submission_awaiting_a_decision_can_be_approved()
    {
        var draft = KybSubmission.StartFor(Guid.CreateVersion7());

        var act = () => draft.Approve(Guid.CreateVersion7(), Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_document_larger_than_the_limit_is_refused()
    {
        var act = () => KybDocument.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), KybDocumentType.TaxIdentification,
            "big.pdf", "key", KybDocumentRules.Pdf, KybDocumentRules.MaxSizeBytes + 1, new string('a', 64));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\ada\\cert.pdf", "cert.pdf")]
    [InlineData("   ", "document")]
    public void A_filename_is_stripped_to_its_own_name(string given, string expected)
    {
        // Nothing builds a path from this — the storage key is generated — but it is shown to a
        // reviewer and may end up in a download header.
        var document = KybDocument.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), KybDocumentType.TaxIdentification,
            given, "key", KybDocumentRules.Pdf, 1024, new string('a', 64));

        document.FileName.Should().Be(expected);
    }
}

public class WalletFundingPolicyTests
{
    private static Agency AgencyWith(AgencyStatus status)
    {
        var agency = Agency.RegisterPrincipal("Test Limited", "test-limited", "NG", "NGN", "Africa/Lagos");

        switch (status)
        {
            case AgencyStatus.Verified: agency.MarkVerified(DateTimeOffset.UtcNow); break;
            case AgencyStatus.Rejected: agency.MarkRejected(); break;
            case AgencyStatus.Suspended: agency.Suspend(); break;
            case AgencyStatus.Terminated: agency.Terminate(); break;
            default: break;
        }

        return agency;
    }

    [Fact]
    public void A_verified_agency_may_fund_its_wallet() =>
        WalletFundingPolicy.For(AgencyWith(AgencyStatus.Verified)).IsAllowed.Should().BeTrue();

    [Theory]
    [InlineData(AgencyStatus.PendingVerification)]
    [InlineData(AgencyStatus.Rejected)]
    [InlineData(AgencyStatus.Suspended)]
    [InlineData(AgencyStatus.Terminated)]
    public void Everyone_else_is_blocked_with_an_explanation(AgencyStatus status)
    {
        var decision = WalletFundingPolicy.For(AgencyWith(status));

        decision.IsAllowed.Should().BeFalse();

        // The FRD asks for the option to be visibly disabled with a reason, not to fail silently
        // when pressed — so there is always something to show.
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unverified_agency_is_told_how_to_unblock_itself() =>
        WalletFundingPolicy.For(AgencyWith(AgencyStatus.PendingVerification))
            .Reason.Should().Contain("KYB");

    [Fact]
    public void A_rejected_agency_is_pointed_at_the_reason() =>
        WalletFundingPolicy.For(AgencyWith(AgencyStatus.Rejected))
            .Reason.Should().Contain("onboarding page");
}
