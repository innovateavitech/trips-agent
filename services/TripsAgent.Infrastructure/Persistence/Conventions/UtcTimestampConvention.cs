using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// Every instant in this system is a <see cref="DateTimeOffset"/> stored as PostgreSQL
/// <c>timestamp with time zone</c>, which Npgsql normalises to UTC on write.
///
/// <see cref="DateTime"/> is rejected outright. It carries a <c>Kind</c> that survives neither a
/// round-trip through the database nor a hop between two servers, so "was this local or UTC?"
/// becomes unanswerable exactly when it matters — a ticket time limit that expires an hour early
/// in Lagos, or a ledger entry dated to the wrong day. <see cref="DateTimeOffset"/> carries its
/// own offset and has no such ambiguity.
///
/// <see cref="DateOnly"/> and <see cref="TimeOnly"/> are untouched: a departure date or a
/// check-in time is a calendar value, not an instant, and has no time zone to get wrong.
/// </summary>
public sealed class UtcTimestampConvention : IModelFinalizingConvention
{
    /// <summary>PostgreSQL type behind a <see cref="DateTimeOffset"/>. Stored as UTC.</summary>
    public const string TimestampColumnType = "timestamp with time zone";

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (clrType == typeof(DateTime))
                {
                    throw new InvalidOperationException(
                        $"""
                         {entityType.DisplayName()}.{property.Name} is a DateTime. This codebase uses
                         DateTimeOffset for every instant, so the offset travels with the value and the
                         database always stores UTC.

                         Fix: change the property to DateTimeOffset (or DateTimeOffset?).
                         If it is a calendar date with no time — a departure date, a date of birth —
                         use DateOnly instead.
                         """);
                }

                if (clrType == typeof(DateTimeOffset))
                {
                    property.SetColumnType(TimestampColumnType);
                }
            }
        }
    }
}
