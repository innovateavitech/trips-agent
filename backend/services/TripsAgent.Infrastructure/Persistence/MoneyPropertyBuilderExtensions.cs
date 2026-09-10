using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// Explicit spelling of the money mapping, for entity configurations.
///
/// <see cref="MoneyMinorConvention"/> already maps anything named <c>*Minor</c>, so most
/// properties need nothing. Use this when the name does not carry the suffix but the value is
/// still money, and you want the intent visible in the configuration file.
/// </summary>
public static class MoneyPropertyBuilderExtensions
{
    /// <summary>Maps a money amount to a <c>bigint</c> column of minor units (kobo).</summary>
    /// <example>
    /// <code>
    /// builder.Property(w => w.BalanceMinor).IsMoneyMinor();
    /// </code>
    /// </example>
    public static PropertyBuilder<long> IsMoneyMinor(this PropertyBuilder<long> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasColumnType(MoneyMinorConvention.MoneyColumnType);
    }

    /// <summary>Maps an optional money amount to a nullable <c>bigint</c> column of minor units.</summary>
    public static PropertyBuilder<long?> IsMoneyMinor(this PropertyBuilder<long?> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasColumnType(MoneyMinorConvention.MoneyColumnType);
    }
}
