using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

internal interface IFakeSupplierClient
{
    public HttpClient Http { get; }

    public Task<HttpResponseMessage> IssueAsync();
}

/// <summary>What an adapter's typed client looks like: it tags its request and sends it, nothing more.</summary>
internal sealed class FakeSupplierClient(HttpClient http) : IFakeSupplierClient
{
    public static readonly Guid SupplierId = Guid.CreateVersion7();

    public HttpClient Http => http;

    public Task<HttpResponseMessage> IssueAsync() =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/v2/ticketing/issue")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }
        .ForSupplierCall(SupplierId, SupplierOperation.Issue, new SupplierCallContext(AgencyId: null)));
}

/// <summary>A hand-written handler — exactly how a home-made retry loop would arrive.</summary>
internal sealed class PassThroughHandler : DelegatingHandler;

/// <summary>
/// The registration path every supplier client goes through: the audit handler attached by
/// construction, the timeout owned by it, and — ADR-0003 — no way to put a retry underneath it.
/// </summary>
public class SupplierHttpClientRegistrationTests
{
    private const string MerchantKey = "fake.merchant.key.value";

    private readonly RecordingSupplierCallRecorder _recorder = new();

    [Fact]
    public async Task A_supplier_client_is_audited_by_construction_including_its_default_headers()
    {
        var network = StubSupplierHandler.Answering(HttpStatusCode.OK, "{}");
        using var provider = Build(network);

        using var response = await provider.GetRequiredService<IFakeSupplierClient>().IssueAsync();

        var capture = _recorder.Captures.Should().ContainSingle().Subject;
        capture.Outcome.Should().Be(SupplierCallOutcome.Succeeded);

        // The merchant key set once at registration reaches the handler, so it can be redacted.
        capture.RequestHeaders.Should().ContainKey("MerchantKey");
        capture.ToApiCall().RequestHeaders.Should().NotContain(MerchantKey);
    }

    [Fact]
    public void The_client_timeout_belongs_to_the_audit_handler_even_if_the_adapter_sets_one()
    {
        using var provider = Build(StubSupplierHandler.Answering(HttpStatusCode.OK, "{}"));

        // Configured as 5 seconds below; forced infinite so it cannot fire before the handler's own
        // timer and turn a timeout into something that looks like a caller giving up.
        provider.GetRequiredService<IFakeSupplierClient>().Http.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Through_the_real_registration_a_timed_out_issue_call_is_sent_exactly_once()
    {
        var network = StubSupplierHandler.NeverAnswering();
        using var provider = Build(network, timeout: TimeSpan.FromMilliseconds(100));

        var issue = () => provider.GetRequiredService<IFakeSupplierClient>().IssueAsync();

        await issue.Should().ThrowAsync<SupplierCallTimeoutException>();
        network.Attempts.Should().Be(1);
        _recorder.Captures.Should().ContainSingle().Which.Outcome.Should().Be(SupplierCallOutcome.Timeout);
    }

    [Fact]
    public void A_standard_resilience_handler_added_to_a_supplier_client_stops_it_being_created()
    {
        using var provider = Build(
            StubSupplierHandler.Answering(HttpStatusCode.OK, "{}"),
            services => services.AddHttpClient<IFakeSupplierClient, FakeSupplierClient>().AddStandardResilienceHandler());

        AssertRefused(provider, "ResilienceHandler");
    }

    [Fact]
    public void A_resilience_handler_added_to_every_client_by_default_is_refused_on_a_supplier_client()
    {
        // The easy one to miss: a well-meant default for the whole process, written for Paystack.
        using var provider = Build(
            StubSupplierHandler.Answering(HttpStatusCode.OK, "{}"),
            services => services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler()));

        AssertRefused(provider, "ResilienceHandler");
    }

    [Fact]
    public void A_hand_written_handler_is_refused_too_because_a_retry_loop_can_live_in_one()
    {
        using var provider = Build(
            StubSupplierHandler.Answering(HttpStatusCode.OK, "{}"),
            services => services.AddHttpClient<IFakeSupplierClient, FakeSupplierClient>()
                .AddHttpMessageHandler(() => new PassThroughHandler()));

        AssertRefused(provider, nameof(PassThroughHandler));
    }

    private static void AssertRefused(ServiceProvider provider, string offendingType)
    {
        var create = () => provider.GetRequiredService<IFakeSupplierClient>();

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one handler, SupplierAuditHandler*")
            .Which.Message.Should().Contain(offendingType).And.Contain("0003-never-retry-ticket-issuance");
    }

    private ServiceProvider Build(
        StubSupplierHandler network,
        Action<IServiceCollection>? alsoConfigure = null,
        TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISupplierCallRecorder>(_recorder);

        services.AddSupplierHttpClient<IFakeSupplierClient, FakeSupplierClient>(
            client =>
            {
                client.BaseAddress = new Uri("https://supplier.test/");
                client.DefaultRequestHeaders.Add("MerchantKey", MerchantKey);
                client.Timeout = TimeSpan.FromSeconds(5);
            },
            timeout);

        // The stub stands in for the network as the primary handler — which the registration allows,
        // because the primary handler is the transport, not something layered on top of it.
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => network));

        alsoConfigure?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
