using Npgsql;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// A principal with two sub-agents and an unrelated principal, on its own database.
/// </summary>
/// <remarks>
/// Shared by the sub-agent network's tests because every one of them needs the same four agencies:
/// the questions worth asking are all of the form "can this one see or change that one's terms?",
/// and they need a sibling and an outsider to be worth asking at all.
/// </remarks>
internal sealed class SubAgentWorld : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly List<AgencySession> _sessions = [];

    public SubAgentWorld(
        PostgresFixture postgres,
        string database,
        Guid principal,
        Guid subAgent,
        Guid sibling,
        Guid outsider)
    {
        _postgres = postgres;
        Database = database;
        Principal = principal;
        SubAgent = subAgent;
        Sibling = sibling;
        Outsider = outsider;
    }

    public string Database { get; }

    /// <summary>The agency at the top of the network. Owns every row the feature writes.</summary>
    public Guid Principal { get; }

    /// <summary>The sub-agent the fixture's scope, override and allowance are about.</summary>
    public Guid SubAgent { get; }

    /// <summary>Another sub-agent under the same principal. Must see none of the first one's terms.</summary>
    public Guid Sibling { get; }

    /// <summary>A principal with a network of its own. Must see none of this one's.</summary>
    public Guid Outsider { get; }

    /// <summary>
    /// A context acting as <paramref name="agencyId"/>, as the policed application role.
    /// </summary>
    /// <param name="rootAgencyId">
    /// The principal above it, for a sub-agent. It is what tells the application a caller is a
    /// sub-agent at all, so passing it wrong makes a sub-agent behave like a principal.
    /// </param>
    public AgencySession ActingAs(Guid agencyId, Guid? rootAgencyId = null)
    {
        var tenancy = TestTenancy.For(agencyId, rootAgencyId);
        var db = _postgres.Connect(Database, tenancy.Tenant, tenancy.Scope);
        var session = new AgencySession(db, tenancy.Tenant, tenancy.Scope);

        _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Runs one statement on a bare connection as the application role, with only
    /// <c>app.agency_id</c> set — no EF, no filters, nothing but the policies.
    /// </summary>
    /// <returns>How many rows it changed.</returns>
    public async Task<int> ExecuteAsAgencyAsync(Guid agencyId, string sql)
    {
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(Database, asApplicationRole: true));
        await connection.OpenAsync();

        await using (var set = connection.CreateCommand())
        {
            set.CommandText = "SELECT set_config('app.agency_id', $1, false)";
            set.Parameters.AddWithValue(agencyId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one column from every row, as the database owner — for asserting on the truth.</summary>
    public async Task<List<string>> AdminListAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(Database, asApplicationRole: false));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>Reads one scalar as the database owner.</summary>
    public async Task<long> AdminCountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(Database, asApplicationRole: false));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }
    }
}

/// <summary>One agency's context, with the tenant and scope it was opened with.</summary>
internal sealed class AgencySession : IAsyncDisposable
{
    public AgencySession(AppDbContext db, TenantContext tenant, PlatformScope scope)
    {
        Db = db;
        Tenant = tenant;
        Scope = scope;
    }

    public AppDbContext Db { get; }

    public TenantContext Tenant { get; }

    public PlatformScope Scope { get; }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
