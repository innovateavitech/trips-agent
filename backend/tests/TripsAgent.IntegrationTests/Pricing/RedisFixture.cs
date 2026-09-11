using StackExchange.Redis;
using Testcontainers.Redis;

namespace TripsAgent.IntegrationTests.Pricing;

/// <summary>A real Redis 7 in a throwaway container, for the markup rule cache.</summary>
/// <remarks>
/// Shared by the tests in a class. Tests stay apart without flushing it because every cache key
/// includes an agency id, and each test creates its own agencies.
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    private ConnectionMultiplexer? _connection;

    /// <summary><c>host:port</c>, as StackExchange.Redis and ConnectionStrings:Redis expect it.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The fixture has not started yet.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
