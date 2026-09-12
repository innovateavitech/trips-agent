using FluentAssertions;
using TripsAgent.Domain.Documents;

namespace TripsAgent.UnitTests.Documents;

/// <summary>
/// Issue #46's rules as the domain holds them: a first issue, a reissue that never touches the
/// original, and a file that is final once rendered.
/// </summary>
public class GeneratedDocumentTests
{
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly Guid Order = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly DocumentRecipient Ada = new(" Ada Obi ", " Ada.Obi@Example.test ");

    [Fact]
    public void A_first_issue_is_issue_one_replaces_nothing_and_waits_to_be_rendered()
    {
        var invoice = GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Invoice, 1), IssuedAt, Order, null, Ada);

        invoice.IssueNumber.Should().Be(1);
        invoice.SupersedesDocumentId.Should().BeNull();
        invoice.Status.Should().Be(DocumentStatus.Pending);
        invoice.OrderId.Should().Be(Order);
        invoice.RecipientName.Should().Be("Ada Obi");
        invoice.RecipientEmail.Should().Be("ada.obi@example.test");
    }

    [Fact]
    public void An_invoice_names_no_line_and_a_voucher_names_exactly_one()
    {
        var invoiceWithLine = () =>
            GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Invoice, 1), IssuedAt, Order, Line, Ada);
        var voucherWithoutLine = () =>
            GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Voucher, 1), IssuedAt, Order, null, Ada);
        var quote = () =>
            GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Quote, 1), IssuedAt, Order, null, Ada);

        invoiceWithLine.Should().Throw<ArgumentException>();
        voucherWithoutLine.Should().Throw<ArgumentException>();
        quote.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_reissue_is_a_new_document_under_a_new_number_and_the_original_is_untouched()
    {
        var original = ReadyVoucher();
        var before = Snapshot(original);

        var reissue = original.Reissue(Number(DocumentType.Voucher, 2), IssuedAt.AddDays(1));

        reissue.Id.Should().NotBe(original.Id);
        reissue.IssueNumber.Should().Be(2);
        reissue.SupersedesDocumentId.Should().Be(original.Id);
        reissue.DocumentNumber.Should().Be("VCH-2026-000002");
        reissue.OrderId.Should().Be(original.OrderId);
        reissue.OrderLineId.Should().Be(original.OrderLineId);
        reissue.RecipientEmail.Should().Be(original.RecipientEmail);
        reissue.Status.Should().Be(DocumentStatus.Pending);
        reissue.AssetId.Should().BeNull("the reissue has a file of its own, drawn afresh");

        Snapshot(original).Should().Be(before, "nothing about the original changes when it is replaced");
    }

    [Fact]
    public void A_reissue_of_a_reissue_is_issue_three()
    {
        var second = ReadyVoucher().Reissue(Number(DocumentType.Voucher, 2), IssuedAt);
        Render(second);

        var third = second.Reissue(Number(DocumentType.Voucher, 3), IssuedAt);

        third.IssueNumber.Should().Be(3);
        third.SupersedesDocumentId.Should().Be(second.Id);
    }

    [Fact]
    public void A_document_with_no_file_yet_cannot_be_reissued()
    {
        var pending = GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Invoice, 1), IssuedAt, Order, null, Ada);

        var reissue = () => pending.Reissue(Number(DocumentType.Invoice, 2), IssuedAt);

        reissue.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_reissue_takes_its_number_from_its_own_sequence()
    {
        var reissue = () => ReadyVoucher().Reissue(Number(DocumentType.Invoice, 2), IssuedAt);

        reissue.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Once_rendered_the_file_is_final()
    {
        var voucher = ReadyVoucher();

        voucher.Invoking(v => v.BeginRender()).Should().Throw<InvalidOperationException>();
        voucher.Invoking(v => v.RecordRenderFailure("late failure")).Should().Throw<InvalidOperationException>();
        voucher.Invoking(v => v.MarkRendered(Guid.CreateVersion7(), new string('c', 64), 1, "voucher.flight", 1, IssuedAt))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Five_failed_attempts_mark_it_failed_and_asking_again_starts_afresh()
    {
        var invoice = GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Invoice, 1), IssuedAt, Order, null, Ada);

        for (var attempt = 1; attempt < GeneratedDocument.MaxRenderAttempts; attempt++)
        {
            invoice.BeginRender();
            invoice.RecordRenderFailure("renderer crashed").Should().BeFalse();
            invoice.Status.Should().Be(DocumentStatus.Pending);
        }

        invoice.BeginRender();
        invoice.RecordRenderFailure("renderer crashed").Should().BeTrue();
        invoice.Status.Should().Be(DocumentStatus.Failed);

        // Asked for again, it gets a fresh run of attempts rather than failing at once.
        invoice.BeginRender();
        invoice.RenderAttempts.Should().Be(1);
        invoice.Status.Should().Be(DocumentStatus.Pending);
    }

    [Fact]
    public void A_failure_that_retrying_cannot_fix_marks_it_failed_at_once()
    {
        var invoice = GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Invoice, 1), IssuedAt, Order, null, Ada);
        invoice.BeginRender();

        invoice.RecordRenderFailure("would carry the platform's brand", permanent: true).Should().BeTrue();

        invoice.Status.Should().Be(DocumentStatus.Failed);
        invoice.RenderAttempts.Should().Be(1);
    }

    [Fact]
    public void Its_file_name_is_its_number_with_nothing_a_file_system_minds()
    {
        var invoice = GeneratedDocument.IssueForOrder(
            Agency, new AllocatedDocumentNumber(DocumentType.Invoice, 2026, 42, "INV/LAGOS/2026/000042"), IssuedAt, Order, null, Ada);

        invoice.FileName.Should().Be("INV-LAGOS-2026-000042.pdf");
    }

    [Fact]
    public void The_first_email_that_carried_it_is_the_one_remembered()
    {
        var voucher = ReadyVoucher();
        var first = Guid.CreateVersion7();

        voucher.RecordEmail(first);
        voucher.RecordEmail(Guid.CreateVersion7());

        voucher.EmailNotificationId.Should().Be(first);
    }

    private static AllocatedDocumentNumber Number(DocumentType type, long sequence) =>
        new(type, 2026, sequence, $"{(type == DocumentType.Invoice ? "INV" : type == DocumentType.Voucher ? "VCH" : "QUO")}-2026-{sequence:D6}");

    private static GeneratedDocument ReadyVoucher()
    {
        var voucher = GeneratedDocument.IssueForOrder(Agency, Number(DocumentType.Voucher, 1), IssuedAt, Order, Line, Ada);
        Render(voucher);
        return voucher;
    }

    private static void Render(GeneratedDocument document)
    {
        document.BeginRender();
        document.MarkRendered(Guid.CreateVersion7(), new string('a', 64), 1_234, "voucher.flight", 1, IssuedAt);
    }

    private static string Snapshot(GeneratedDocument document) =>
        string.Join(
            '|',
            document.Id,
            document.DocumentNumber,
            document.IssueNumber,
            document.SupersedesDocumentId,
            document.Status,
            document.AssetId,
            document.Checksum,
            document.SizeBytes,
            document.TemplateKey,
            document.RenderedAt,
            document.RecipientEmail);
}
