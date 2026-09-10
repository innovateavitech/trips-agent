using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// Enforces CLAUDE.md rule 2 — money is <c>bigint</c> minor units, never a decimal — at the
/// level where it cannot be forgotten: the model itself.
///
/// Any property whose name ends in <c>Minor</c> must be a <see cref="long"/> and is mapped to
/// a <c>bigint</c> column. A <c>decimal</c>, <c>double</c> or <c>float</c> named <c>*Minor</c>
/// fails model building with an explanation, so the mistake surfaces the first time anyone runs
/// the app — not months later when a reconciliation job finds the missing kobo.
///
/// Why kobo and not naira: ₦1,500.00 is stored as 150000. Floating-point money loses fractions
/// on arithmetic, and in a double-entry ledger a lost fraction is unrecoverable — the debits
/// stop equalling the credits and nothing tells you which row is wrong.
/// </summary>
public sealed class MoneyMinorConvention : IModelFinalizingConvention
{
    /// <summary>Column naming suffix that marks a value as money in minor units.</summary>
    public const string MoneySuffix = "Minor";

    /// <summary>PostgreSQL type every money column maps to. 64-bit, exact, no rounding.</summary>
    public const string MoneyColumnType = "bigint";

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (!property.Name.EndsWith(MoneySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (clrType != typeof(long))
                {
                    throw new InvalidOperationException(
                        $"""
                         {entityType.DisplayName()}.{property.Name} is a {clrType.Name}, but its name ends
                         in '{MoneySuffix}', which this codebase reserves for money in minor units.

                         Money is always a long holding minor units (kobo), never a decimal or a double:

                             decimal price = 1500.00m;   // wrong
                             long priceMinor = 150000;   // right — ₦1,500.00

                         Fix: change the property to long (or long?), or rename it if it is not money.
                         Reasoning: CLAUDE.md rule 2.
                         """);
                }

                property.SetColumnType(MoneyColumnType);
            }
        }
    }
}
