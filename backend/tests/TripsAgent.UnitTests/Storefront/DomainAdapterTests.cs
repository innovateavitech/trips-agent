using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TripsAgent.Application.Storefront;
using TripsAgent.Domain.Storefront;
using TripsAgent.Infrastructure.Storefront;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// The pieces behind an agency's own domain that need no database: where a registered domain ends, how a
/// resolver's answer is read, what each lookup means for verification, and the settings that pick adapters.
/// </summary>
public class DomainAdapterTests
{
    private const string Resolver = "resolver.example";

    [Theory]
    [InlineData("www.lekkihorizon.com", "lekkihorizon.com", false)]
    [InlineData("lekkihorizon.com", "lekkihorizon.com", true)]
    [InlineData("book.lekkihorizon.com.ng", "lekkihorizon.com.ng", false)]
    [InlineData("lekkihorizon.com.ng", "lekkihorizon.com.ng", true)]
    [InlineData("com.ng", "com.ng", true)]
    [InlineData("tours.agency.co.uk", "agency.co.uk", false)]
    public void The_registered_part_of_a_hostname_is_found(string hostname, string registered, bool isApex)
    {
        RegistrableDomains.Of(hostname).Should().Be(registered);
        RegistrableDomains.IsApex(hostname).Should().Be(isApex);
    }

    [Theory]
    [InlineData("_storefront-verify.www.lekkihorizon.com", "www.lekkihorizon.com", "_storefront-verify.www")]
    [InlineData("www.lekkihorizon.com", "www.lekkihorizon.com", "www")]
    [InlineData("_storefront-verify.book.lekkihorizon.com.ng", "book.lekkihorizon.com.ng", "_storefront-verify.book")]
    [InlineData("lekkihorizon.com", "lekkihorizon.com", "@")]
    public void The_host_field_gets_the_name_without_the_domain_on_the_end(string recordName, string hostname, string expected) =>
        RegistrableDomains.HostLabel(recordName, hostname).Should().Be(expected);

    [Fact]
    public void A_TXT_answer_keeps_every_value_at_the_name_asked_quotes_and_all()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"_storefront-verify.www.example.com.","type":16,"TTL":300,"data":"\"first\""},
              {"name":"_storefront-verify.www.example.com.","type":16,"TTL":300,"data":"\"second\" \"half\""}
            ]}
            """;

        var result = DnsOverHttpsResolver.Parse(json, "_storefront-verify.www.example.com", DnsOverHttpsResolver.TxtType, Resolver);

        result.Status.Should().Be(DnsLookupStatus.Answered);
        result.Values.Should().Equal("\"first\"", "\"second\" \"half\"");
        result.Resolver.Should().Be(Resolver);
    }

    [Fact]
    public void A_CNAME_answer_counts_only_the_record_at_the_name_asked_not_where_the_chain_leads()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"www.example.com.","type":5,"TTL":300,"data":"sites.example.net."},
              {"name":"sites.example.net.","type":5,"TTL":300,"data":"edge.example.org."},
              {"name":"edge.example.org.","type":1,"TTL":300,"data":"192.0.2.10"}
            ]}
            """;

        var result = DnsOverHttpsResolver.Parse(json, "www.example.com", DnsOverHttpsResolver.CnameType, Resolver);

        result.Values.Should().Equal("sites.example.net.");
    }

    [Theory]
    [InlineData("""{"Status":3}""", DnsLookupStatus.NotFound)]
    [InlineData("""{"Status":0,"Answer":[{"name":"www.example.com.","type":1,"data":"192.0.2.10"}]}""", DnsLookupStatus.NotFound)]
    [InlineData("""{"Status":2}""", DnsLookupStatus.ServerFailure)]
    [InlineData("""{"Status":5}""", DnsLookupStatus.Error)]
    [InlineData("<html>not json</html>", DnsLookupStatus.Error)]
    [InlineData("[]", DnsLookupStatus.Error)]
    public void Every_other_answer_is_an_outcome_never_an_exception(string json, DnsLookupStatus expected) =>
        DnsOverHttpsResolver.Parse(json, "www.example.com", DnsOverHttpsResolver.CnameType, Resolver).Status.Should().Be(expected);

    [Theory]
    [InlineData(DnsLookupStatus.Answered, true, DnsCheckOutcome.Match)]
    [InlineData(DnsLookupStatus.Answered, false, DnsCheckOutcome.Mismatch)]
    [InlineData(DnsLookupStatus.NotFound, false, DnsCheckOutcome.NotFound)]
    [InlineData(DnsLookupStatus.Timeout, false, DnsCheckOutcome.Timeout)]
    [InlineData(DnsLookupStatus.ServerFailure, false, DnsCheckOutcome.ServerFailure)]
    [InlineData(DnsLookupStatus.Error, false, DnsCheckOutcome.Error)]
    public void Each_way_a_lookup_ends_is_recorded_as_its_own_outcome(DnsLookupStatus status, bool matches, DnsCheckOutcome expected)
    {
        var values = status == DnsLookupStatus.Answered ? new[] { "value" } : Array.Empty<string>();

        DomainVerifier.Outcome(new DnsLookupResult(status, values, Resolver), _ => matches).Should().Be(expected);
    }

    [Theory]
    [InlineData("Storefront:Dns:Mode", "Carrier-pigeon")]
    [InlineData("Storefront:Dns:DohEndpoint", "http://resolver.example/dns-query")]
    public void A_mistyped_DNS_setting_stops_startup(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value })
            .Build();

        var act = () => StorefrontRegistration.ReadDnsSettings(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void Unset_DNS_and_certificate_modes_are_left_for_the_environment_to_decide()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Storefront:Dns:Mode"] = " " })
            .Build();

        StorefrontRegistration.ReadDnsSettings(configuration).Mode.Should().BeNull();
        StorefrontRegistration.ReadCertificateSettings(configuration).Mode.Should().BeNull();
    }

    [Fact]
    public async Task Without_an_issuer_a_certificate_request_fails_softly_so_it_backs_off_and_alerts()
    {
        var result = await new UnavailableCertificateIssuer().RequestAsync("www.example.com");

        result.Should().BeOfType<CertificateResult.Failed>().Which.Permanent.Should().BeFalse();
    }
}
