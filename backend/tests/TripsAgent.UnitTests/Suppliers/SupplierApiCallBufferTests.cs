using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>Keeps every log line, so a test can see what an operator would.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception), exception));
}

/// <summary>
/// The hand-off from the request path to the writer. Issue #39: it must never slow a request, and
/// a call it cannot hold must never vanish silently.
/// </summary>
public class SupplierApiCallBufferTests
{
    private const string PassportNumber = "A01234567";
    private const string MerchantKey = "fake.merchant.key.value";

    private readonly ListLogger<SupplierApiCallBuffer> _logger = new();

    [Fact]
    public void A_full_buffer_loses_the_new_call_loudly_and_counts_it_but_never_blocks()
    {
        var buffer = new SupplierApiCallBuffer(Options.Create(new SupplierApiCallOptions { QueueCapacity = 2 }), _logger);

        buffer.Record(Capture());
        buffer.Record(Capture());
        var lost = Capture(SupplierOperation.Issue);
        buffer.Record(lost);

        buffer.LostCount.Should().Be(1);
        buffer.Reader.Count.Should().Be(2);

        var line = _logger.Lines.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.Message.Should().Contain("NOT recorded").And.Contain("buffer is full")
            .And.Contain(lost.CallId.ToString()).And.Contain("Issue").And.Contain("order-7f3a");

        // The log line is the only evidence left, and it must not become a leak in its place.
        line.Message.Should().NotContain(PassportNumber).And.NotContain(MerchantKey).And.NotContain("abcdef");
    }

    [Fact]
    public void A_call_made_after_shutdown_began_is_reported_rather_than_left_unread()
    {
        var buffer = new SupplierApiCallBuffer(Options.Create(new SupplierApiCallOptions()), _logger);

        buffer.Close();
        buffer.Record(Capture());

        buffer.LostCount.Should().Be(1);
        _logger.Lines.Should().ContainSingle().Which.Message.Should().Contain("shutting down");
    }

    [Fact]
    public void Infrastructure_wires_the_recorder_to_the_buffer_and_runs_the_writer_in_every_host()
    {
        using var provider = Build([]);

        provider.GetRequiredService<ISupplierCallRecorder>()
            .Should().BeSameAs(provider.GetRequiredService<SupplierApiCallBuffer>());
        provider.GetServices<IHostedService>().Should().ContainSingle(service => service is SupplierApiCallWriterService);
    }

    [Theory]
    [InlineData("SupplierApiCalls:QueueCapacity", "0")]
    [InlineData("SupplierApiCalls:BatchSize", "0")]
    public void A_zero_capacity_or_batch_is_refused_at_startup(string key, string value)
    {
        using var provider = Build(new Dictionary<string, string?> { [key] = value });

        var read = () => provider.GetRequiredService<IOptions<SupplierApiCallOptions>>().Value;

        read.Should().Throw<OptionsValidationException>();
    }

    private static SupplierCallCapture Capture(SupplierOperation operation = SupplierOperation.Search) => new()
    {
        CallId = Guid.CreateVersion7(),
        SupplierId = Guid.CreateVersion7(),
        AgencyId = Guid.CreateVersion7(),
        Operation = operation,
        HttpMethod = "POST",
        Endpoint = "/api/v2/ticketing/issue?token=abcdef",
        RequestHeaders = new Dictionary<string, string> { ["MerchantKey"] = MerchantKey },
        RequestBody = $$"""{"DocNumber":"{{PassportNumber}}"}""",
        ResponseStatusCode = 200,
        ResponseBody = "{}",
        LatencyMs = 812,
        Outcome = SupplierCallOutcome.Succeeded,
        OccurredAt = DateTimeOffset.UtcNow,
        CorrelationId = "order-7f3a",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings[$"ConnectionStrings:{DependencyInjection.PostgresConnectionName}"] =
            "Host=never.connected.invalid;Database=none;Username=none;Password=none";

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider();
    }
}
