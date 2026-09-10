using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TripsAgent.Domain.Common;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// Rules the money columns must obey, expressed once so a test can assert them against the
/// finalised EF Core model rather than against our good intentions.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no clever builder extension that renames columns for you. Name the
/// property with a <c>Minor</c> suffix — <c>NetRateMinor</c>, <c>WalletBalanceMinor</c> — and
/// snake-case naming turns it into <c>net_rate_minor</c> on its own. The suffix is visible in
/// the C#, visible in psql, and there is no convention to learn.
/// </para>
/// <para>
/// <c>MoneyColumnNamingTests</c> fails the build if a <see cref="Money"/> property lands in a
/// column without the suffix, or if any property anywhere maps to a decimal column type.
/// </para>
/// </remarks>
public static class MoneyConventions
{
    /// <summary>The suffix every money column carries, so its unit is never in doubt.</summary>
    public const string MinorUnitSuffix = "_minor";

    /// <summary>The one column type money is ever stored in.</summary>
    public const string StoreType = "bigint";

    /// <summary>
    /// Column types that must never appear in the model. A <c>numeric</c> price is how kobo go
    /// missing; a <c>float</c> price is how they go missing faster.
    /// </summary>
    public static readonly string[] ForbiddenStoreTypes =
    [
        "numeric",
        "decimal",
        "money",
        "real",
        "double precision",
    ];

    /// <summary>
    /// Maps every <see cref="Money"/> property in the model to a <c>bigint</c> column.
    /// </summary>
    /// <remarks>
    /// Applied once, model-wide, from <c>AppDbContext.ConfigureConventions</c> — so a new entity
    /// gets it for free and cannot land money in a <c>numeric</c> column by forgetting a line of
    /// per-entity configuration. Exposed as a method so the tests exercise this exact code
    /// rather than a copy of it.
    /// </remarks>
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<Money>()
            .HaveConversion<MoneyConverter>()
            .HaveColumnType(StoreType);
    }

    /// <summary>True when the property holds a <see cref="Money"/> value.</summary>
    public static bool IsMoney(IProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.ClrType == typeof(Money) || property.ClrType == typeof(Money?);
    }
}
