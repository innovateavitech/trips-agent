using System.Security.Claims;
using FluentAssertions;
using TripsAgent.Infrastructure.Scheduling;

namespace TripsAgent.UnitTests.Scheduling;

/// <summary>
/// The Hangfire dashboard is not a read-only status page. Anyone who reaches it can requeue,
/// delete and trigger jobs, and some of those jobs refund customers and move money between
/// wallets. So the interesting cases here are all the ones that must be refused.
/// </summary>
public class HangfireDashboardPolicyTests
{
    private const string AdminRole = "platform-admin";

    private static HangfireDashboardPolicy Production() => new(AdminRole, allowUnauthenticatedLocalRequests: false);

    private static HangfireDashboardPolicy Development() => new(AdminRole, allowUnauthenticatedLocalRequests: true);

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role)),
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

    [Fact]
    public void An_anonymous_request_should_be_refused()
    {
        Production().IsAllowed(Anonymous(), isLocalRequest: false).Should().BeFalse();
    }

    [Fact]
    public void A_request_with_no_principal_at_all_should_be_refused()
    {
        Production().IsAllowed(user: null, isLocalRequest: false).Should().BeFalse();
    }

    [Fact]
    public void A_signed_in_user_in_the_wrong_role_should_be_refused()
    {
        // An agent is a paying customer, not staff. Nothing an agent holds should ever open a
        // dashboard that can replay another agency's payment jobs.
        Production().IsAllowed(SignedIn("agent-owner"), isLocalRequest: false).Should().BeFalse();
    }

    [Fact]
    public void A_signed_in_platform_admin_should_be_allowed()
    {
        Production().IsAllowed(SignedIn(AdminRole), isLocalRequest: false).Should().BeTrue();
    }

    [Fact]
    public void A_local_request_should_be_refused_in_production()
    {
        // The case that catches teams out. Behind a reverse proxy or an ingress controller every
        // request arrives from the loopback address, so "allow local requests" — Hangfire's own
        // default — publishes the dashboard to the internet.
        Production().IsAllowed(Anonymous(), isLocalRequest: true).Should().BeFalse();
    }

    [Fact]
    public void A_local_request_should_be_allowed_in_development()
    {
        // Identity does not exist yet (issue #12), so without this a developer could not open the
        // dashboard at all. The Api only ever passes true when the environment is Development.
        Development().IsAllowed(Anonymous(), isLocalRequest: true).Should().BeTrue();
    }

    [Fact]
    public void A_remote_request_should_be_refused_even_in_development()
    {
        Development().IsAllowed(Anonymous(), isLocalRequest: false).Should().BeFalse();
    }
}
