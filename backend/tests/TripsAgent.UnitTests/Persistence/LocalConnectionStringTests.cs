using System.Text.Json;
using FluentAssertions;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// The local connection string lives in three places, and they have to agree.
/// </summary>
/// <remarks>
/// <para>
/// <c>docker compose up</c> creates the database from <c>.env.example</c>; <c>dotnet run</c>
/// connects with <c>appsettings.Development.json</c>; <c>./scripts/ef.sh update</c> connects with
/// <see cref="AppDbContextFactory"/>. They drifted once already — compose created
/// <c>trips_agent</c> as <c>trips</c> while the code asked for <c>tripsagent</c> as
/// <c>postgres</c> — and a new developer following the README got an authentication failure on
/// their very first <c>-- migrate</c>, with nothing pointing at the cause.
/// </para>
/// <para>
/// Reads the real files from the repository rather than copies, so the test fails the moment
/// someone edits one without the others.
/// </para>
/// </remarks>
public class LocalConnectionStringTests
{
    [Fact]
    public void Appsettings_should_connect_to_the_database_docker_compose_creates()
    {
        Appsettings().Should().Be(EnvExample(),
            "`dotnet run` must reach the database that `docker compose up` created from .env.example");
    }

    [Fact]
    public void The_design_time_factory_should_connect_to_the_database_docker_compose_creates()
    {
        AppDbContextFactory.LocalDevelopmentConnectionString.Should().Be(EnvExample(),
            "`./scripts/ef.sh update` must reach the same database as the running API");
    }

    private static string EnvExample()
    {
        const string key = "ConnectionStrings__Postgres=";

        var line = File.ReadLines(Path.Combine(RepositoryRoot(), ".env.example"))
            .SingleOrDefault(candidate => candidate.StartsWith(key, StringComparison.Ordinal));

        line.Should().NotBeNull($".env.example should define {key.TrimEnd('=')}");
        return line![key.Length..].Trim();
    }

    private static string Appsettings()
    {
        var path = Path.Combine(
            RepositoryRoot(), "backend", "services", "TripsAgent.Api", "appsettings.Development.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("ConnectionStrings").GetProperty("Postgres").GetString()!;
    }

    /// <summary>Walks up from the test binaries until it finds the folder holding .env.example.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".env.example")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find .env.example above {AppContext.BaseDirectory}. These tests read the real " +
            "repository files, so they must run from inside a checkout.");
    }
}
