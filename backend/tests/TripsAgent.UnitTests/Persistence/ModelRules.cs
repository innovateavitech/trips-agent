using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// The schema rules from CLAUDE.md, expressed as checks over a finalised EF Core model.
/// </summary>
/// <remarks>
/// Written against <see cref="IModel"/> rather than against a live database, so they run in
/// milliseconds with no Docker and still catch the mistake at the moment it is made — which is
/// while someone is adding an entity, not after a migration has shipped.
/// </remarks>
internal static class ModelRules
{
    /// <summary>A single rule breach, phrased so the failure message tells you what to do.</summary>
    internal sealed record Violation(string Table, string Column, string Problem)
    {
        public override string ToString() => $"{Table}.{Column}: {Problem}";
    }

    /// <summary>
    /// Money must be a <c>bigint</c> column whose name ends in <c>_minor</c>.
    /// </summary>
    internal static List<Violation> MoneyColumns(IModel model)
    {
        var violations = new List<Violation>();

        foreach (var (entity, property, table, column) in Columns(model))
        {
            if (!MoneyConventions.IsMoney(property))
            {
                continue;
            }

            if (!column.EndsWith(MoneyConventions.MinorUnitSuffix, StringComparison.Ordinal))
            {
                violations.Add(new Violation(
                    table,
                    column,
                    $"holds Money but the column name does not end in '{MoneyConventions.MinorUnitSuffix}'. "
                    + $"Rename the property on {entity} to end in 'Minor' — e.g. NetRateMinor — so the "
                    + "unit is visible in psql as well as in the C#."));
            }

            var storeType = property.GetColumnType();
            if (!string.Equals(storeType, MoneyConventions.StoreType, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new Violation(
                    table,
                    column,
                    $"holds Money but maps to '{storeType}' instead of '{MoneyConventions.StoreType}'."));
            }
        }

        return violations;
    }

    /// <summary>
    /// Nothing anywhere may map to a decimal or floating-point column.
    /// </summary>
    /// <remarks>
    /// This is the backstop for CLAUDE.md rule 2. Money should already be a
    /// <c>TripsAgent.Domain.Common.Money</c>, but a stray <c>decimal Price</c> on a new entity
    /// would compile perfectly happily — and lose kobo in production.
    /// </remarks>
    internal static List<Violation> ForbiddenNumericColumns(IModel model)
    {
        var violations = new List<Violation>();

        foreach (var (_, property, table, column) in Columns(model))
        {
            var storeType = property.GetColumnType();

            if (string.IsNullOrEmpty(storeType))
            {
                continue;
            }

            // Compare against the bare type name: Npgsql renders precision as "numeric(18,2)".
            var bareType = storeType.Split('(')[0].Trim();

            if (MoneyConventions.ForbiddenStoreTypes.Contains(bareType, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new Violation(
                    table,
                    column,
                    $"maps to '{storeType}'. Money is a bigint of minor units — use "
                    + "TripsAgent.Domain.Common.Money. A non-money quantity that genuinely needs "
                    + "fractions should say so in a code review first."));
            }
        }

        return violations;
    }

    /// <summary>
    /// Points in time are <see cref="DateTimeOffset"/>, never <see cref="DateTime"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>DateTime</c> carries no offset, so "was this ticket issued before the time limit?"
    /// stops being answerable the moment two servers disagree about their local zone. Npgsql maps
    /// <see cref="DateTimeOffset"/> to <c>timestamp with time zone</c> and stores UTC.
    /// </para>
    /// <para>
    /// <see cref="UtcTimestampConvention"/> is the first line of defence and fails the model build
    /// outright. This rule stays as the backstop: it holds for a context that forgets to register
    /// the convention, and it reports every offending column at once with a friendlier message
    /// than an exception thrown on the first one.
    /// </para>
    /// </remarks>
    internal static List<Violation> NaiveTimestamps(IModel model)
    {
        var violations = new List<Violation>();

        foreach (var (entity, property, table, column) in Columns(model))
        {
            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

            if (clrType == typeof(DateTime))
            {
                violations.Add(new Violation(
                    table,
                    column,
                    $"is a DateTime on {entity}. Use DateTimeOffset so the instant is unambiguous "
                    + "across time zones; the column becomes 'timestamp with time zone' and holds UTC."));
            }
        }

        return violations;
    }

    /// <summary>Every mapped scalar column in the model, with the table it belongs to.</summary>
    private static IEnumerable<(string Entity, IProperty Property, string Table, string Column)> Columns(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            if (tableName is null)
            {
                continue;   // owned types sharing their owner's table, and query types
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject);

                if (column is not null)
                {
                    yield return (entityType.DisplayName(), property, tableName, column);
                }
            }
        }
    }
}
