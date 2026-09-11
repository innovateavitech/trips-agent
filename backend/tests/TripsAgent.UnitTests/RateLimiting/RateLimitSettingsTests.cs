using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Infrastructure.RateLimiting;

namespace TripsAgent.UnitTests.RateLimiting;

/// <summary>
/// Limits are configuration, not constants — and configuration that is wrong must stop the API
/// starting, not quietly fall back to something nobody chose.
/// </summary>
public class RateLimitSettingsTests
{
    [Fact]
    public void With_nothing_configured_rate_limiting_is_on_with_the_defaults()
    {
        var settings = RateLimitingRegistration.ReadSettings(Configuration([]));

        settings.Enabled.Should().BeTrue("a deployment that forgot to configure it must still be protected");
        settings.Rules.Should().BeEquivalentTo(RateLimitSettings.Defaults);
    }

    [Fact]
    public void Every_policy_has_a_default()
    {
        RateLimitSettings.Defaults.Keys.Should().BeEquivalentTo(RateLimitPolicyNames.All);
    }

    [Fact]
    public void The_request_types_that_cost_money_or_leak_information_are_tighter_than_the_default()
    {
        var defaults = RateLimitSettings.Defaults;
        var general = PerMinute(defaults[RateLimitPolicyNames.Default]);

        foreach (var tight in new[]
                 {
                     RateLimitPolicyNames.Login,
                     RateLimitPolicyNames.Registration,
                     RateLimitPolicyNames.OtpResend,
                     RateLimitPolicyNames.ForgotPassword,
                     RateLimitPolicyNames.Search,
                 })
        {
            PerMinute(defaults[tight]).Should().BeLessThan(general, $"{tight} should be tighter than the default");
        }
    }

    [Fact]
    public void Only_an_explicit_false_switches_it_off()
    {
        RateLimitingRegistration.ReadSettings(Configuration(new() { ["RateLimiting:Enabled"] = "false" }))
            .Enabled.Should().BeFalse();

        RateLimitingRegistration.ReadSettings(Configuration(new() { ["RateLimiting:Enabled"] = "" }))
            .Enabled.Should().BeTrue();
    }

    [Fact]
    public void A_policy_can_be_changed_per_environment()
    {
        var settings = RateLimitingRegistration.ReadSettings(Configuration(new()
        {
            ["RateLimiting:Policies:Login:PermitLimit"] = "7",
            ["RateLimiting:Policies:Login:Window"] = "00:02:00",
        }));

        settings.RuleFor(RateLimitPolicyNames.Login).Should().Be(new RateLimitRule(7, TimeSpan.FromMinutes(2)));
        settings.RuleFor(RateLimitPolicyNames.Search).Should().Be(RateLimitSettings.Defaults[RateLimitPolicyNames.Search]);
    }

    [Fact]
    public void A_misspelt_policy_stops_startup_rather_than_leaving_the_real_one_on_its_default()
    {
        var act = () => RateLimitingRegistration.ReadSettings(Configuration(new()
        {
            ["RateLimiting:Policies:Logn:PermitLimit"] = "5",
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Logn*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("ten")]
    public void A_permit_limit_that_is_not_a_positive_whole_number_stops_startup(string value)
    {
        var act = () => RateLimitingRegistration.ReadSettings(Configuration(new()
        {
            ["RateLimiting:Policies:Search:PermitLimit"] = value,
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting__Policies__Search__PermitLimit*");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("00:00:00")]
    [InlineData("2.00:00:00")]
    [InlineData("soon")]
    public void A_window_outside_one_second_to_one_day_stops_startup(string value)
    {
        var act = () => RateLimitingRegistration.ReadSettings(Configuration(new()
        {
            ["RateLimiting:Policies:Default:Window"] = value,
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting__Policies__Default__Window*");
    }

    [Fact]
    public void The_agency_ceiling_is_not_a_policy_an_endpoint_can_choose()
    {
        RateLimitPolicyNames.IsEndpointPolicy(RateLimitPolicyNames.Agency).Should().BeFalse();
        RateLimitPolicyNames.IsEndpointPolicy(RateLimitPolicyNames.Login).Should().BeTrue();
        RateLimitPolicyNames.IsEndpointPolicy("Logn").Should().BeFalse();
    }

    private static double PerMinute(RateLimitRule rule) => rule.PermitLimit / rule.Window.TotalMinutes;

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
