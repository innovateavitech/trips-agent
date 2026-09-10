using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform database.
///
/// There are no <c>DbSet</c> properties yet — entities arrive with the schema issues that follow
/// (agencies #10, identity #13, the ledger #22). Mappings are discovered automatically from
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes in
/// <c>Persistence/Configurations/</c>, so adding an entity never means editing this file.
///
/// Domain types stay free of EF attributes: an architecture test fails the build if
/// <c>TripsAgent.Domain</c> so much as references EF Core.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up every IEntityTypeConfiguration<T> in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // These run over the finished model and reject mappings that would be bugs.
        // Registered here rather than in DI so that anything building the model — the app,
        // `dotnet ef`, a test — gets the same rules.
        TripsAgentConventions.Apply(configurationBuilder);
    }
}
