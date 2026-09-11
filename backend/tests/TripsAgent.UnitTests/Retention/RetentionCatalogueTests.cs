using FluentAssertions;
using TripsAgent.Application.Retention;
using TripsAgent.Infrastructure.Retention;

namespace TripsAgent.UnitTests.Retention;

/// <summary>
/// The guard in front of the purge job. The integration tests prove the database agrees; these prove
/// the job refuses a bad rule before it gets anywhere near one.
/// </summary>
public class RetentionCatalogueTests
{
    public static TheoryData<string> ProtectedTables() => new(RetentionCatalogue.ProtectedTables);

    [Fact]
    public void The_records_that_must_outlive_this_job_are_all_protected()
    {
        // Named rather than derived from the list, so removing one from the list fails here.
        RetentionCatalogue.ProtectedTables.Should().Contain(
        [
            "payments.ledger_entries",
            "payments.ledger_accounts",
            "payments.wallet_transactions",
            "payments.payment_transactions",
            "payments.payment_webhook_events",
            "orders.orders",
            "orders.order_lines",
            "orders.order_status_history",
            "documents.generated_documents",
            "platform.audit_logs",
            "supplier.supplier_bookings",
            "supplier.supplier_status_polls",
        ]);
    }

    [Fact]
    public void No_rule_the_job_ships_with_targets_a_protected_table()
    {
        var rules = RetentionCatalogue.Rules(new DataRetentionOptions());

        rules.Select(rule => rule.Table).Should().NotIntersectWith(RetentionCatalogue.ProtectedTables);

        var check = () => RetentionCatalogue.EnsureAllowed(rules);
        check.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(ProtectedTables))]
    public void A_rule_that_deletes_from_a_protected_table_is_refused(string table)
    {
        var act = () => RetentionCatalogue.EnsureAllowed([new RetentionRule(table, RetentionAction.Delete, TimeSpan.FromDays(1), "true")]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{table}*financial or audit*");
    }

    [Theory]
    [MemberData(nameof(ProtectedTables))]
    public void A_rule_that_anonymises_a_protected_table_is_refused(string table)
    {
        var act = () => RetentionCatalogue.EnsureAllowed(
            [new RetentionRule(table, RetentionAction.Anonymise, TimeSpan.FromDays(1), "true", "x = NULL")]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{table}*");
    }

    [Theory]
    [InlineData("PAYMENTS.LEDGER_ENTRIES")]
    [InlineData("ledger_entries")]
    [InlineData("\"payments\".\"ledger_entries\"")]
    [InlineData("payments.ledger_entries; DROP TABLE x")]
    [InlineData("payments . ledger_entries")]
    public void A_table_name_that_could_dodge_the_protected_list_is_refused(string table)
    {
        // Every one of these would reach the ledger in PostgreSQL while not matching the list as text.
        var act = () => RetentionCatalogue.EnsureAllowed([new RetentionRule(table, RetentionAction.Delete, TimeSpan.FromDays(1), "true")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to run*");
    }

    [Theory]
    [InlineData("identity.users")]              // kept
    [InlineData("supplier.search_requests")]    // not yet enforced
    [InlineData("payments.some_future_table")]  // not classified at all
    public void A_rule_for_a_table_not_classified_as_purgeable_is_refused(string table)
    {
        // The allowlist half of the guard: a new financial table nobody remembered to protect is still
        // out of reach, because nobody has classified it as purgeable either.
        var act = () => RetentionCatalogue.EnsureAllowed([new RetentionRule(table, RetentionAction.Delete, TimeSpan.FromDays(1), "true")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not classify*");
    }

    [Fact]
    public void Deleting_from_a_table_classified_for_anonymising_only_is_refused()
    {
        var act = () => RetentionCatalogue.EnsureAllowed(
            [new RetentionRule("orders.order_travellers", RetentionAction.Delete, TimeSpan.FromDays(1), "true")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders.order_travellers*");
    }

    [Fact]
    public void An_anonymise_rule_that_does_not_say_what_to_clear_is_refused()
    {
        var act = () => RetentionCatalogue.EnsureAllowed(
            [new RetentionRule("orders.order_travellers", RetentionAction.Anonymise, TimeSpan.FromDays(1), "true")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*which columns*");
    }

    [Fact]
    public void Every_table_is_classified_once()
    {
        RetentionCatalogue.Tables.Select(table => table.Table).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_purged_or_anonymised_table_is_actually_enforced_by_a_rule()
    {
        var rules = RetentionCatalogue.Rules(new DataRetentionOptions());
        var ruled = rules.Select(rule => rule.Table).ToHashSet(StringComparer.Ordinal);

        foreach (var table in RetentionCatalogue.Tables.Where(t => t.Treatment == RetentionTreatment.Purged))
        {
            var enforcedBy = table.PurgedWith ?? table.Table;
            ruled.Should().Contain(enforcedBy, $"{table.Table} is classified as purged, so something must purge it");
        }

        foreach (var table in RetentionCatalogue.Tables.Where(t => t.Treatment == RetentionTreatment.Anonymised))
        {
            rules.Should().Contain(rule => rule.Table == table.Table && rule.Action == RetentionAction.Anonymise);
        }
    }

    [Fact]
    public void Windows_come_from_configuration()
    {
        var rules = RetentionCatalogue.Rules(new DataRetentionOptions { LoginAttemptDays = 12, TravelDocumentDays = 45 });

        rules.Single(rule => rule.Table == "identity.login_attempts").Window.Should().Be(TimeSpan.FromDays(12));
        rules.Where(rule => rule.Table is "supplier.passenger_documents" or "orders.order_travellers")
            .Should().OnlyContain(rule => rule.Window == TimeSpan.FromDays(45));
    }

    [Fact]
    public void Every_rule_filters_on_the_cutoff()
    {
        // A predicate without @cutoff would match every row regardless of age.
        RetentionCatalogue.Rules(new DataRetentionOptions())
            .Should().OnlyContain(rule => rule.Predicate.Contains("@cutoff", StringComparison.Ordinal));
    }

    [Fact]
    public void The_first_release_is_a_dry_run()
    {
        new DataRetentionOptions().DryRun.Should().BeTrue("nothing is deleted until someone decides it should be");
    }

    [Theory]
    [InlineData(false, "delete", null, "retention.deleted")]
    [InlineData(false, "anonymise", null, "retention.anonymised")]
    [InlineData(false, "drop-partitions", null, "retention.partitions_dropped")]
    [InlineData(true, "delete", null, "retention.dry_run")]
    [InlineData(true, "delete", "boom", "retention.failed")]
    [InlineData(false, "delete", "boom", "retention.failed")]
    public void Audit_actions_say_what_happened(bool dryRun, string action, string? error, string expected)
    {
        var outcome = new RetentionTableOutcome("identity.login_attempts", action, 3, "90 days", DateTimeOffset.UnixEpoch, error);

        DataRetentionPurge.ActionName(outcome, dryRun).Should().Be(expected);
    }
}
