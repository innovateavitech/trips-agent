using Microsoft.EntityFrameworkCore;

namespace TripsAgent.Infrastructure.Persistence.Conventions;

/// <summary>
/// The model-wide rules this codebase enforces, in one place.
///
/// <see cref="AppDbContext"/> applies these, and so do their tests — which is the point. If the
/// tests registered their own copy of the list, a convention could be dropped from the real
/// context and every test would still pass.
/// </summary>
public static class TripsAgentConventions
{
    /// <summary>Registers every Trips Agent model convention.</summary>
    public static ModelConfigurationBuilder Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Add(_ => new MoneyMinorConvention());
        configurationBuilder.Conventions.Add(_ => new UtcTimestampConvention());

        return configurationBuilder;
    }
}
