using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Billing;
using TripsAgent.Domain.Billing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Billing;

/// <summary>
/// Money adds up: an invoice's lines equal its total, in minor units.
/// </summary>
/// <remarks>
/// The acceptance test for the money half of issues 64 and 65. Checked against the database rather
/// than the domain, because the domain's own arithmetic is already covered by a unit test and the
/// thing that actually goes wrong is a row written by something that skipped it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SubscriptionInvoiceIntegrityTests
{
    private readonly PostgresFixture _postgres;

    public SubscriptionInvoiceIntegrityTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Every_invoice_the_billing_run_raises_adds_up_in_the_database()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");

        // Deliberately not a round number of naira: ₦18,750.55. A bug that works in major units
        // somewhere loses the 55 kobo, and this is where it shows.
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 1_875_055);

        await world.SubscribeAsync(agencyId, tierId);
        await world.AddCardAsync(agencyId);

        world.Gateway.WillSucceed();
        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        var invoices = await world.InvoicesOfAsync(agencyId);

        invoices.Should().ContainSingle();
        invoices[0].TotalMinor.AmountMinor.Should().Be(1_875_055);
        invoices[0].AddsUp.Should().BeTrue();
        invoices[0].Lines.Sum(line => line.AmountMinor.AmountMinor).Should().Be(1_875_055);

        using var scope = world.Tenancy.Scope.Enter("test assertion — sums invoice lines in the database");

        var mismatches = await world.Db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                  FROM billing.subscription_invoices AS i
                 WHERE i.total_minor <> (
                       SELECT coalesce(sum(l.amount_minor), 0)
                         FROM billing.subscription_invoice_lines AS l
                        WHERE l.invoice_id = i.id)
                """)
            .ToListAsync();

        mismatches[0].Should().Be(0);
    }

    /// <summary>
    /// The database's own refusal. A deferred constraint trigger rather than a CHECK, because the
    /// total and the lines live in different tables and the invoice row is necessarily written
    /// first — an immediate check would reject every correct insert.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_an_invoice_whose_total_disagrees_with_its_lines()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);
        var subscription = await world.SubscribeAsync(agencyId, tierId);

        using var scope = world.Tenancy.Scope.Enter("test — writes an invoice that does not add up");

        var now = world.Clock.GetUtcNow();

        // Written straight through SQL, so the domain's own recomputation is bypassed entirely —
        // which is exactly the case the trigger exists for.
        var writing = async () => await world.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO billing.subscription_invoices
                (id, agency_id, subscription_id, invoice_number, currency, status, total_minor,
                 period_start, period_end, issued_at, due_at, created_at, updated_at)
            VALUES ({0}, {1}, {2}, 'TRIPS-INV-TEST-1', 'NGN', 'Open', 9999,
                 {3}, {4}, {3}, {3}, {3}, {3});

            INSERT INTO billing.subscription_invoice_lines
                (id, agency_id, invoice_id, description, quantity, unit_amount_minor, amount_minor,
                 created_at, updated_at)
            VALUES ({5}, {1}, {0}, 'A line that does not match the total', 1, 1234, 1234, {3}, {3});
            """,
            Guid.CreateVersion7(), agencyId, subscription.Id, now, now.AddMonths(1), Guid.CreateVersion7());

        await writing.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.MessageText.Contains("lines come to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_whose_amount_is_not_its_quantity_times_its_unit_price_is_refused()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);
        var subscription = await world.SubscribeAsync(agencyId, tierId);

        using var scope = world.Tenancy.Scope.Enter("test — writes a line whose arithmetic is wrong");

        var now = world.Clock.GetUtcNow();
        var invoiceId = Guid.CreateVersion7();

        var writing = async () => await world.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO billing.subscription_invoices
                (id, agency_id, subscription_id, invoice_number, currency, status, total_minor,
                 period_start, period_end, issued_at, due_at, created_at, updated_at)
            VALUES ({0}, {1}, {2}, 'TRIPS-INV-TEST-2', 'NGN', 'Open', 5000,
                 {3}, {4}, {3}, {3}, {3}, {3});

            INSERT INTO billing.subscription_invoice_lines
                (id, agency_id, invoice_id, description, quantity, unit_amount_minor, amount_minor,
                 created_at, updated_at)
            VALUES ({5}, {1}, {0}, 'Three seats billed as one', 3, 1000, 5000, {3}, {3});
            """,
            invoiceId, agencyId, subscription.Id, now, now.AddMonths(1), Guid.CreateVersion7());

        await writing.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.ConstraintName == "ck_subscription_invoice_lines_amount_is_product");
    }

    [Fact]
    public async Task A_charge_attempt_can_never_be_rewritten()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var agencyId = await world.AddAgencyAsync("Acme Travel Limited", "acme");
        var tierId = await world.AddPublishedTierAsync("growth", "Growth", 2_500_000);

        await world.SubscribeAsync(agencyId, tierId);
        await world.AddCardAsync(agencyId);

        world.Clock.Advance(TimeSpan.FromDays(32));
        await world.Billing.RunAsync();

        var attempts = await world.AttemptsOfAsync(agencyId);
        attempts.Should().NotBeEmpty();

        using var scope = world.Tenancy.Scope.Enter("test — tries to rewrite a charge attempt");

        var rewriting = async () => await world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE billing.subscription_charge_attempts SET outcome = 'Succeeded' WHERE id = {0};",
            attempts[0].Id);

        // Two defences, and this connection meets the first: the application role has no UPDATE
        // grant on the table, so it is refused with 42501 before the trigger is reached. The
        // trigger is the backstop for the table's owner, whom a REVOKE does not bind.
        await rewriting.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(exception => exception.SqlState == "42501"
                             || exception.MessageText.Contains("append-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invoice_number_is_unique_across_the_whole_platform()
    {
        await using var world = await BillingWorld.CreateAsync(_postgres);

        var first = await world.NumbersNextInvoiceAsync();
        var second = await world.NumbersNextInvoiceAsync();

        first.Should().NotBe(second);
        first.Should().MatchRegex(@"^TRIPS-INV-\d{4}-\d{6}$");
    }
}
