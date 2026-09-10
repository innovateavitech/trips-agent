using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// Builds an EF Core model for a single throwaway entity, using the same provider and the same
/// conventions <see cref="AppDbContext"/> uses.
///
/// No database is involved. EF works out the mapping — table names, column names, column types —
/// entirely in memory, and only opens a connection when you actually query. That is what makes
/// these fast unit tests rather than integration tests: the connection string below points at
/// nothing and never needs to.
///
/// The conventions come from <see cref="TripsAgentConventions.Apply"/> and the naming and
/// provider setup from <see cref="AppDbContextOptions.Configure"/> — the very same calls the
/// real context makes. If a convention were dropped from the real context, these tests would
/// stop seeing it too, which is the whole point of not re-listing them here.
/// </summary>
internal static class ModelHarness
{
    /// <summary>
    /// Well-formed but deliberately unreachable. Model building never dials out; if some future
    /// change makes it try, the failure will be loud rather than a silent hit on a real database.
    /// </summary>
    private const string UnusedConnectionString =
        "Host=model.building.invalid;Database=never_connected;Username=none;Password=none";

    /// <summary>Builds the model for <typeparamref name="TEntity"/> and returns its mapping.</summary>
    /// <param name="configure">
    /// Optional extra mapping, standing in for what an <c>IEntityTypeConfiguration&lt;T&gt;</c>
    /// would do in <c>Persistence/Configurations/</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown by a convention when the entity breaks a rule — a <c>*Minor</c> property that is not
    /// a <see cref="long"/>, or a <see cref="DateTime"/> anywhere. The tests rely on this.
    /// </exception>
    public static IEntityType BuildEntityType<TEntity>(Action<EntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        var builder = new DbContextOptionsBuilder<HarnessDbContext<TEntity>>();
        AppDbContextOptions.Configure(builder, UnusedConnectionString);

        using var context = new HarnessDbContext<TEntity>(builder.Options, configure);

        // Touching .Model is what forces EF to build and finalise the model, which is when the
        // conventions run and when a bad mapping throws.
        return context.Model.FindEntityType(typeof(TEntity))
               ?? throw new InvalidOperationException($"{typeof(TEntity).Name} was not mapped.");
    }

    /// <summary>Convenience: the column a property maps to, or null if the property is not mapped.</summary>
    public static IProperty Property<TEntity>(
        string propertyName,
        Action<EntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BuildEntityType<TEntity>(configure).FindProperty(propertyName)
           ?? throw new InvalidOperationException($"{typeof(TEntity).Name}.{propertyName} was not mapped.");

    /// <summary>
    /// Every message in an exception chain, joined.
    ///
    /// EF Core is free to wrap what a convention throws, so asserting on
    /// <c>exception.Message</c> alone makes a test that passes today and breaks on an EF
    /// upgrade for no real reason. Asserting the chain contains the explanation is what we
    /// actually care about: the developer who hits this must be told what to change.
    /// </summary>
    public static string FlattenMessages(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    /// <summary>
    /// A context holding exactly one entity, registered without a <c>DbSet</c> so that the table
    /// name comes from the entity type itself rather than from a property name.
    /// </summary>
    private sealed class HarnessDbContext<TEntity>(
        DbContextOptions<HarnessDbContext<TEntity>> options,
        Action<EntityTypeBuilder<TEntity>>? configure)
        : DbContext(options)
        where TEntity : class
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Register first, configure second. Folding these into one expression with `?.`
            // would skip the registration entirely whenever configure is null, because the
            // null-conditional operator does not evaluate its argument.
            var entity = modelBuilder.Entity<TEntity>();
            configure?.Invoke(entity);
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            TripsAgentConventions.Apply(configurationBuilder);
        }
    }
}
