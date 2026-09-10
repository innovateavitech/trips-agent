using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// The registration entry point the Api and the Worker both call, and the design-time factory
/// the <c>dotnet ef</c> tooling calls. None of these open a connection.
/// </summary>
public class PersistenceRegistrationTests
{
    private const string UnusedConnectionString =
        "Host=model.building.invalid;Database=never_connected;Username=none;Password=none";

    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    [Fact]
    public void AddPersistence_should_register_a_Postgres_backed_context()
    {
        using var provider = new ServiceCollection()
            .AddPersistence(UnusedConnectionString)
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        context.Database.ProviderName.Should().Be(NpgsqlProvider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_connection_string_should_fail_loudly_at_startup(string? connectionString)
    {
        var act = () => new ServiceCollection().AddPersistence(connectionString);

        // Failing at startup beats failing on the first request that touches the database: a
        // container that will not boot is obvious, one that 500s under load is not. And the
        // message is the point — whoever hits this usually just has not started Postgres yet.
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll(
                DependencyInjection.ConnectionStringName,
                "appsettings.Development.json",
                "docker compose up -d");
    }

    [Fact]
    public void The_design_time_factory_should_build_a_context_with_no_configuration_at_all()
    {
        // This is what `dotnet ef migrations add` relies on. When it breaks, the symptom is an
        // opaque tooling error at the worst moment, so it is worth a test of its own.
        using var context = new AppDbContextFactory().CreateDbContext([]);

        context.Database.ProviderName.Should().Be(NpgsqlProvider);
    }

    [Fact]
    public void The_tooling_and_the_app_should_read_the_same_connection_string()
    {
        // AppDbContextFactory reads ConnectionStrings__Postgres; the app reads
        // ConnectionStrings:Postgres. If these drift, `dotnet ef` and the running app quietly
        // talk to different databases — and the migration you just applied is not where you
        // think it is.
        AppDbContextFactory.ConnectionStringVariable
            .Should().Be($"ConnectionStrings__{DependencyInjection.ConnectionStringName}");
    }
}
