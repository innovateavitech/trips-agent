using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TripsAgent.Infrastructure.Networking;

namespace TripsAgent.UnitTests.Networking;

/// <summary>
/// The known-proxy allowlist. Every mistake it refuses is a way of trusting <c>X-Forwarded-For</c>
/// from someone who is not our proxy — which lets a caller choose the address the rate limiter sees.
/// </summary>
public class TrustedProxiesTests
{
    [Fact]
    public void Nothing_configured_trusts_no_extra_proxies()
    {
        var settings = TrustedProxies.Read(Configuration([]));

        settings.KnownProxies.Should().BeEmpty();
        settings.KnownNetworks.Should().BeEmpty();
        settings.ForwardLimit.Should().Be(1);
    }

    [Fact]
    public void Addresses_and_networks_are_read_from_comma_separated_lists()
    {
        var settings = TrustedProxies.Read(Configuration(new()
        {
            ["ForwardedHeaders:KnownProxies"] = "10.0.0.5, 10.0.0.6;2001:db8::10",
            ["ForwardedHeaders:KnownNetworks"] = "10.20.0.0/16,2001:db8:1::/48",
            ["ForwardedHeaders:ForwardLimit"] = "2",
        }));

        settings.KnownProxies.Should().Equal(
            IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6"), IPAddress.Parse("2001:db8::10"));
        settings.KnownNetworks.Should().Equal(
            IPNetwork.Parse("10.20.0.0/16"), IPNetwork.Parse("2001:db8:1::/48"));
        settings.ForwardLimit.Should().Be(2);
    }

    [Theory]
    [InlineData("10.5")]    // IPAddress.TryParse reads this as 10.0.0.5 — a surprise in an allowlist
    [InlineData("10")]
    [InlineData("load-balancer.internal")]
    public void An_address_that_is_not_written_out_in_full_is_refused(string value)
    {
        var act = () => TrustedProxies.Read(Configuration(new() { ["ForwardedHeaders:KnownProxies"] = value }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Trusting_every_address_on_the_internet_is_refused(string value)
    {
        // This is the mistake the allowlist exists to prevent, written as configuration.
        var act = () => TrustedProxies.Read(Configuration(new() { ["ForwardedHeaders:KnownNetworks"] = value }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*every address*");
    }

    [Theory]
    [InlineData("64.0.0.0/2")]
    [InlineData("2000::/3")]
    public void A_network_far_wider_than_any_proxy_fleet_is_refused(string value)
    {
        var act = () => TrustedProxies.Read(Configuration(new() { ["ForwardedHeaders:KnownNetworks"] = value }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*KnownNetworks*");
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.1/8")]  // host bits set: probably meant 10.0.0.1, or 10.0.0.0/8 — either way, ask
    public void A_network_that_is_not_clean_CIDR_is_refused(string value)
    {
        var act = () => TrustedProxies.Read(Configuration(new() { ["ForwardedHeaders:KnownNetworks"] = value }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*CIDR*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("many")]
    public void A_forward_limit_outside_one_to_five_is_refused(string value)
    {
        var act = () => TrustedProxies.Read(Configuration(new() { ["ForwardedHeaders:ForwardLimit"] = value }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardLimit*");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
