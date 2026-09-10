using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// Issue #8: <c>AppDbContext</c> registered with Npgsql and the <c>snake_case</c> naming
/// convention.
///
/// Naming looks cosmetic and is not. PostgreSQL folds unquoted identifiers to lower case, so a
/// PascalCase table has to be quoted in every hand-written query and every psql session, forever.
/// One missed quote is a runtime error in a place nobody tested.
/// </summary>
public class AppDbContextOptionsTests
{
    // WalletLedgerEntry lives in TestEntities.cs.

    [Fact]
    public void Tables_should_be_named_in_snake_case()
    {
        var entityType = ModelHarness.BuildEntityType<WalletLedgerEntry>();

        entityType.GetTableName().Should().Be("wallet_ledger_entry");
    }

    [Theory]
    [InlineData(nameof(WalletLedgerEntry.Id), "id")]
    [InlineData(nameof(WalletLedgerEntry.AgencyId), "agency_id")]
    [InlineData(nameof(WalletLedgerEntry.AmountMinor), "amount_minor")]
    public void Columns_should_be_named_in_snake_case(string propertyName, string expectedColumn)
    {
        var property = ModelHarness.Property<WalletLedgerEntry>(propertyName);

        property.GetColumnName().Should().Be(expectedColumn);
    }

    [Fact]
    public void The_migrations_history_table_should_be_snake_case_too()
    {
        // EF's default is __EFMigrationsHistory, which would be the one PascalCase table in an
        // otherwise snake_case database — and the one you have to quote when debugging a
        // half-applied deploy, which is exactly when you least want a surprise.
        AppDbContextOptions.MigrationsHistoryTable.Should().Be("__ef_migrations_history");
    }

    [Fact]
    public void Configure_should_reject_a_missing_connection_string()
    {
        var act = () => AppDbContextOptions.Configure(new DbContextOptionsBuilder(), "   ");

        act.Should().Throw<ArgumentException>();
    }
}
