using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// Refuses to build a model containing a <see cref="DateTime"/>, and pins every
/// <see cref="DateTimeOffset"/> to <c>timestamp with time zone</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>DateTime</c>'s <see cref="DateTimeKind"/> survives neither a round trip through the
/// database nor a hop between servers, so "was this local or UTC?" becomes unanswerable exactly
/// when it matters — a ticket time limit expiring an hour early in Lagos, or a ledger entry
/// dated to the wrong day. <see cref="DateTimeOffset"/> carries the offset with the value, and
/// Npgsql refuses to store one that is not UTC, so the rule holds by construction.
/// </para>
/// <para>
/// This fails while the model is being built — the first time anyone opens the context, runs
/// <c>dotnet ef</c>, or starts the API — and the message names the entity and property. That is
/// the point of doing it here rather than only in a test: the failure lands next to the mistake.
/// <c>ModelRules.NaiveTimestamps</c> stays as the belt-and-braces check for a model built
/// without this convention registered.
/// </para>
/// <para>
/// <see cref="DateOnly"/> and <see cref="TimeOnly"/> are deliberately untouched. A departure date
/// or a check-in time is a calendar value, not an instant, and has no time zone to get wrong; a
/// blanket rule over everything date-shaped would force them into the wrong type.
/// </para>
/// </remarks>
public sealed class UtcTimestampConvention : IModelFinalizingConvention
{
    /// <summary>The one column type an instant is ever stored in.</summary>
    public const string TimestampColumnType = "timestamp with time zone";

    /// <summary>
    /// Registers the convention, alongside the money rules, for a context's model.
    /// </summary>
    /// <remarks>
    /// Exposed as a method so the tests register it exactly the way <see cref="AppDbContext"/>
    /// does rather than through a copy of that line.
    /// </remarks>
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Add(_ => new UtcTimestampConvention());
    }

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            // Declared, not inherited: an inherited property is declared on the base entity type,
            // which this loop visits in its own right. Reporting it twice would be noise.
            foreach (var property in entityType.GetDeclaredProperties())
            {
                ProcessProperty(entityType, property);
            }
        }
    }

    private static void ProcessProperty(IConventionEntityType entityType, IConventionProperty property)
    {
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        if (clrType == typeof(DateTime))
        {
            throw new InvalidOperationException(
                $"{entityType.DisplayName()}.{property.Name} is a DateTime. Use DateTimeOffset so "
                + "the instant is unambiguous across time zones; the column becomes "
                + $"'{TimestampColumnType}' and holds UTC. A calendar date with no time of day "
                + "should be a DateOnly instead.");
        }

        if (clrType == typeof(DateTimeOffset))
        {
            // Convention-source configuration, so an entity that genuinely needs something else
            // can still say so with an explicit HasColumnType and win. This is the default Npgsql
            // would pick anyway — stating it means a provider default changing underneath us
            // cannot silently move the column to a type that drops the offset.
            property.SetColumnType(TimestampColumnType);
        }
    }
}
