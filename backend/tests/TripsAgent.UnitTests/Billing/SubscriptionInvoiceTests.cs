using FluentAssertions;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Billing;

/// <summary>
/// An invoice's total is the sum of its lines, in minor units, always.
/// </summary>
/// <remarks>
/// The database says the same thing with a deferred constraint trigger. Both, because this is the
/// number an agency is asked to pay and the one it will argue about: an invoice whose total does
/// not match its own lines is not defensible in a dispute, whichever of the two is right.
/// </remarks>
public class SubscriptionInvoiceTests
{
    private static readonly Guid Agency = Guid.Parse("0197b000-0000-7000-8000-00000000000a");
    private static readonly Guid SubscriptionId = Guid.Parse("0197b000-0000-7000-8000-00000000000b");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);

    private static SubscriptionInvoice Raise() =>
        SubscriptionInvoice.Raise(
            Agency, SubscriptionId, "TRIPS-INV-2026-000001", "NGN",
            Now, Now.AddMonths(1), Now, Now);

    [Fact]
    public void An_empty_invoice_totals_nothing()
    {
        var invoice = Raise();

        invoice.TotalMinor.Should().Be(Money.Zero);
        invoice.AddsUp.Should().BeTrue();
    }

    [Fact]
    public void The_total_is_the_sum_of_the_lines()
    {
        var invoice = Raise();

        // ₦25,000.00 and ₦1,875.00 — the second deliberately not a round number of naira, because
        // the bug this guards against is somebody working in major units somewhere.
        invoice.AddLine("Growth plan — March 2026", 1, new Money(2_500_000));
        invoice.AddLine("Additional seats", 3, new Money(187_500));

        invoice.TotalMinor.AmountMinor.Should().Be(2_500_000 + (187_500 * 3));
        invoice.AddsUp.Should().BeTrue();
        invoice.Recompute().Should().Be(invoice.TotalMinor);
    }

    [Fact]
    public void A_line_multiplies_its_unit_price_by_its_quantity_exactly_once()
    {
        var invoice = Raise();

        var line = invoice.AddLine("Additional seats", 7, new Money(123_457));

        line.AmountMinor.AmountMinor.Should().Be(123_457 * 7);
        line.UnitAmountMinor.AmountMinor.Should().Be(123_457);
        line.Quantity.Should().Be(7);
    }

    [Fact]
    public void Paying_allocates_a_receipt_number_that_never_changes()
    {
        var invoice = Raise();
        invoice.AddLine("Growth plan — March 2026", 1, new Money(2_500_000));

        var payment = Guid.CreateVersion7();
        invoice.MarkPaid(Now.AddDays(1), payment, "TRIPS-RCT-2026-000001");

        invoice.Status.Should().Be(SubscriptionInvoiceStatus.Paid);
        invoice.ReceiptNumber.Should().Be("TRIPS-RCT-2026-000001");
        invoice.PaymentTransactionId.Should().Be(payment);

        // The billing job may be replayed after a crash between the charge and the commit. A second
        // receipt number for one payment is a reconciliation problem nobody enjoys.
        invoice.MarkPaid(Now.AddDays(2), Guid.CreateVersion7(), "TRIPS-RCT-2026-000002");

        invoice.ReceiptNumber.Should().Be("TRIPS-RCT-2026-000001");
        invoice.PaidAt.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void An_invoice_that_has_been_sent_cannot_grow_another_line()
    {
        var invoice = Raise();
        invoice.AddLine("Growth plan — March 2026", 1, new Money(2_500_000));
        invoice.MarkPaid(Now.AddDays(1), Guid.CreateVersion7(), "TRIPS-RCT-2026-000001");

        invoice.Invoking(paid => paid.AddLine("Something else", 1, new Money(100)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Paid*");
    }

    [Fact]
    public void A_paid_invoice_is_never_written_off_voided_or_reopened()
    {
        var invoice = Raise();
        invoice.AddLine("Growth plan — March 2026", 1, new Money(2_500_000));
        invoice.MarkPaid(Now.AddDays(1), Guid.CreateVersion7(), "TRIPS-RCT-2026-000001");

        invoice.MarkPastDue("a stale job");
        invoice.WriteOff("a stale job");

        invoice.Status.Should().Be(SubscriptionInvoiceStatus.Paid);

        invoice.Invoking(paid => paid.Void("a stale job"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Refund it*", "money that moved is recorded, never erased");
    }

    [Fact]
    public void A_failed_charge_leaves_the_invoice_payable()
    {
        var invoice = Raise();
        invoice.AddLine("Growth plan — March 2026", 1, new Money(2_500_000));

        invoice.MarkPastDue("The bank declined the payment.");

        invoice.Status.Should().Be(SubscriptionInvoiceStatus.PastDue);
        invoice.IsPayable.Should().BeTrue("the agency can still pay it on the hosted page");
        invoice.StatusReason.Should().Be("The bank declined the payment.");
    }
}
