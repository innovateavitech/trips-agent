using FluentAssertions;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Assets;

/// <summary>The asset state machine, and the one rule it exists for: nothing unscanned is served.</summary>
public class AssetTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ reserving

    [Fact]
    public void The_storage_key_is_derived_from_ids_not_from_the_filename()
    {
        var asset = Reserve("../../etc/passwd");

        asset.StorageKey.Should().Be($"assets/{AgencyId:N}/{asset.Id:N}/upload");
        asset.StorageKey.Should().NotContain("passwd");
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\ada\Pictures\beach.jpg", "beach.jpg")]
    [InlineData("   ", "upload")]
    [InlineData("folder/", "upload")]
    public void The_filename_is_reduced_to_a_bare_name(string given, string kept) =>
        Reserve(given).FileName.Should().Be(kept);

    [Fact]
    public void A_reserved_asset_starts_pending_and_unservable()
    {
        var asset = Reserve();

        asset.Status.Should().Be(AssetStatus.AwaitingUpload);
        asset.ScanStatus.Should().Be(AssetScanStatus.Pending);
        asset.IsServable.Should().BeFalse();
    }

    // ------------------------------------------------------------------ serving

    [Fact]
    public void Only_an_asset_scanned_clean_and_processed_is_servable()
    {
        var asset = Reserve();
        asset.IsServable.Should().BeFalse("nothing has arrived");

        asset.RecordUpload(MediaTypes.Jpeg, 1_000);
        asset.IsServable.Should().BeFalse("it has arrived but is not scanned");

        asset.TryBeginProcessing(Now).Should().BeTrue();
        asset.RecordCleanScan(Now);
        asset.IsServable.Should().BeFalse("scanned clean, but its renditions do not exist yet");

        asset.MarkReady(400, 200, Now);
        asset.IsServable.Should().BeTrue();
    }

    [Fact]
    public void An_unscanned_asset_cannot_be_marked_ready()
    {
        var asset = Processing();

        var act = () => asset.MarkReady(400, 200, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Pending*");
        asset.IsServable.Should().BeFalse();
    }

    [Fact]
    public void An_asset_the_scanner_could_not_check_cannot_be_marked_ready()
    {
        var asset = Processing();
        asset.RecordUnscannable(Now);

        // "We did not manage to check" is not "we checked and it was fine".
        var act = () => asset.MarkReady(400, 200, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unscannable*");
    }

    [Fact]
    public void A_quarantined_asset_is_never_servable_and_says_why()
    {
        var asset = Processing();

        asset.Quarantine("Win.Test.EICAR_HDB-1", Now);

        asset.Status.Should().Be(AssetStatus.Quarantined);
        asset.ScanStatus.Should().Be(AssetScanStatus.Infected);
        asset.IsServable.Should().BeFalse();
        asset.FailureReason.Should().Contain("virus scanner");
        FluentActions.Invoking(() => asset.MarkReady(1, 1, Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_scanner_finding_longer_than_the_column_is_truncated_rather_than_failing_the_save()
    {
        var asset = Processing();

        asset.Quarantine(new string('x', 500), Now);

        asset.ScanSignature.Should().HaveLength(AssetRules.MaxScanSignatureLength);
    }

    // ------------------------------------------------------------------ the pipeline claim

    [Fact]
    public void A_second_run_cannot_claim_an_asset_while_the_first_holds_the_lease()
    {
        var asset = Uploaded();

        asset.TryBeginProcessing(Now).Should().BeTrue();
        asset.TryBeginProcessing(Now.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void A_claim_whose_lease_ran_out_can_be_taken_over()
    {
        var asset = Uploaded();
        asset.TryBeginProcessing(Now).Should().BeTrue();

        // What a Worker that died mid-run leaves behind.
        asset.TryBeginProcessing(Now + AssetRules.ProcessingLease + TimeSpan.FromSeconds(1)).Should().BeTrue();
    }

    [Fact]
    public void Every_claim_moves_the_concurrency_token()
    {
        var asset = Uploaded();
        var before = asset.Version;

        asset.TryBeginProcessing(Now);

        asset.Version.Should().Be(before + 1);
    }

    [Fact]
    public void An_unavailable_scanner_releases_the_claim_so_the_retry_can_run()
    {
        var asset = Processing();

        asset.RecordUnscannable(Now);

        asset.ProcessingClaimedUntil.Should().BeNull();
        asset.TryBeginProcessing(Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(AssetStatus.Ready)]
    [InlineData(AssetStatus.Failed)]
    [InlineData(AssetStatus.Quarantined)]
    public void A_finished_asset_has_nothing_to_claim(AssetStatus finished)
    {
        var asset = Processing();

        switch (finished)
        {
            case AssetStatus.Ready:
                asset.RecordCleanScan(Now);
                asset.MarkReady(1, 1, Now);
                break;
            case AssetStatus.Failed:
                asset.MarkFailed("broken");
                break;
            default:
                asset.Quarantine(null, Now);
                break;
        }

        asset.TryBeginProcessing(Now.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void An_upload_cannot_be_recorded_twice()
    {
        var asset = Uploaded();

        var act = () => asset.RecordUpload(MediaTypes.Png, 5);

        act.Should().Throw<InvalidOperationException>();
    }

    // ------------------------------------------------------------------ helpers

    private static Asset Reserve(string fileName = "beach.jpg") =>
        Asset.Reserve(AgencyId, AssetPurpose.ProductMedia, fileName, Now.AddMinutes(15));

    private static Asset Uploaded()
    {
        var asset = Reserve();
        asset.RecordUpload(MediaTypes.Jpeg, 1_000);
        return asset;
    }

    private static Asset Processing()
    {
        var asset = Uploaded();
        asset.TryBeginProcessing(Now);
        return asset;
    }
}

public class AssetRulesTests
{
    [Fact]
    public void No_purpose_allows_more_than_the_absolute_ceiling()
    {
        foreach (var purpose in Enum.GetValues<AssetPurpose>())
        {
            AssetRules.MaxSizeBytes(purpose).Should().BeLessThanOrEqualTo(AssetRules.AbsoluteMaxSizeBytes);
        }
    }

    [Fact]
    public void A_logo_is_an_image_and_nothing_else()
    {
        AssetRules.IsAllowedContentType(AssetPurpose.AgencyLogo, MediaTypes.Png).Should().BeTrue();
        AssetRules.IsAllowedContentType(AssetPurpose.AgencyLogo, MediaTypes.Pdf).Should().BeFalse();
        AssetRules.IsAllowedSize(AssetPurpose.AgencyLogo, (2L * 1024 * 1024) + 1).Should().BeFalse();
    }

    [Fact]
    public void An_attachment_may_be_a_pdf()
    {
        AssetRules.IsAllowedContentType(AssetPurpose.Attachment, MediaTypes.Pdf).Should().BeTrue();
        AssetRules.IsImage(MediaTypes.Pdf).Should().BeFalse();
    }

    [Fact]
    public void An_unrecognised_file_is_allowed_for_nothing()
    {
        foreach (var purpose in Enum.GetValues<AssetPurpose>())
        {
            AssetRules.IsAllowedContentType(purpose, null).Should().BeFalse();
        }
    }

    [Fact]
    public void Every_key_leads_with_the_agency_and_never_carries_user_input()
    {
        var agency = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();

        string[] keys =
        [
            AssetRules.UploadKey(agency, asset),
            AssetRules.VariantKey(agency, asset, AssetVariantKind.Thumbnail),
            AssetRules.VerbatimKey(agency, asset, MediaTypes.Pdf),
        ];

        keys.Should().OnlyContain(key => key.StartsWith($"assets/{agency:N}/{asset:N}/", StringComparison.Ordinal));
        keys.Should().OnlyHaveUniqueItems("the raw upload and every copy made from it live at different keys");
    }

    [Fact]
    public void The_copy_of_a_scanned_file_is_never_at_the_upload_key()
    {
        // The upload key stays writable through its presigned URL; the served copy must not be.
        var agency = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();

        AssetRules.VerbatimKey(agency, asset, MediaTypes.Pdf).Should().NotBe(AssetRules.UploadKey(agency, asset));
        AssetRules.VerbatimKey(agency, asset, "APPLICATION/PDF").Should().EndWith(".pdf");
    }
}
