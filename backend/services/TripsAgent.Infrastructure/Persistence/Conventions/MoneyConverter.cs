using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TripsAgent.Domain.Common;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// Maps <see cref="Money"/> to a plain <c>bigint</c> column holding minor units.
/// </summary>
/// <remarks>
/// Registered globally in <c>AppDbContext.ConfigureConventions</c>, so any property typed
/// <see cref="Money"/> gets it automatically — no per-entity wiring, and no chance of one
/// forgotten configuration silently landing money in a <c>numeric</c> column.
/// </remarks>
public sealed class MoneyConverter : ValueConverter<Money, long>
{
    public MoneyConverter()
        : base(money => money.AmountMinor, amountMinor => new Money(amountMinor))
    {
    }
}
