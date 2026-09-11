using FluentAssertions;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Assets;

/// <summary>
/// The one exception to "nothing unscanned is served" (#18): a PDF the platform rendered itself
/// (#46). The exception has to stay exactly that narrow — no uploaded file may ever use it.
/// </summary>
public class GeneratedAssetTests
{
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_rendered_document_is_servable_and_says_it_was_not_scanned_rather_than_clean()
    {
        var asset = Asset.RecordGenerated(
            Agency, "INV-2026-000001.pdf", "documents/a/b.pdf", MediaTypes.Pdf, 48_000, new string('f', 64), Now);

        asset.IsServable.Should().BeTrue();
        asset.Purpose.Should().Be(AssetPurpose.GeneratedDocument);
        asset.Status.Should().Be(AssetStatus.Ready);
        asset.ScanStatus.Should().Be(AssetScanStatus.NotRequired, "'we made it' is a different claim from 'we scanned it'");
    }

    [Fact]
    public void Nothing_can_be_uploaded_as_a_generated_document()
    {
        var reserve = () => Asset.Reserve(Agency, AssetPurpose.GeneratedDocument, "invoice.pdf", Now.AddMinutes(15));

        reserve.Should().Throw<ArgumentException>();
        AssetRules.IsUploadable(AssetPurpose.GeneratedDocument).Should().BeFalse();
        AssetRules.IsUploadable(AssetPurpose.Attachment).Should().BeTrue();
        AssetRules.IsUploadable((AssetPurpose)99).Should().BeFalse();
    }

    [Fact]
    public void An_upload_is_still_served_only_once_it_is_scanned_clean()
    {
        var upload = Asset.Reserve(Agency, AssetPurpose.Attachment, "supplier-invoice.pdf", Now.AddMinutes(15));
        upload.RecordUpload(MediaTypes.Pdf, 1_000);
        upload.TryBeginProcessing(Now).Should().BeTrue();

        upload.IsServable.Should().BeFalse();

        upload.RecordCleanScan(Now);
        upload.ReplaceOriginal("assets/a/b/original.pdf", MediaTypes.Pdf, 1_000, new string('e', 64));
        upload.MarkReady(null, null, Now);

        upload.IsServable.Should().BeTrue();
    }

    [Fact]
    public void Each_render_of_a_document_is_stored_under_ids_never_under_its_printed_number()
    {
        var agency = Guid.Parse("0192d3a4-0000-7000-8000-000000000001");
        var document = Guid.Parse("0192d3a4-0000-7000-8000-000000000002");
        var render = Guid.Parse("0192d3a4-0000-7000-8000-000000000003");

        AssetRules.GeneratedDocumentKey(agency, document, render)
            .Should().Be($"documents/{agency:N}/{document:N}/{render:N}.pdf");
    }
}
