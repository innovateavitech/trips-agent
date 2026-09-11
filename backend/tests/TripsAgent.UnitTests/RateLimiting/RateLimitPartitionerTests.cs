using System.Net;
using System.Security.Claims;
using FluentAssertions;
using TripsAgent.Application.Identity;
using TripsAgent.Application.RateLimiting;

namespace TripsAgent.UnitTests.RateLimiting;

/// <summary>
/// Whose count a request lands in. The three partitions exist for different attackers and different
/// innocent bystanders, so each rule here is one of them.
/// </summary>
public class RateLimitPartitionerTests
{
    private static readonly Guid AgencyId = Guid.Parse("0191f0a0-0000-7000-8000-000000000001");

    [Fact]
    public void An_anonymous_request_is_counted_by_its_address()
    {
        var caller = RateLimitPartitioner.Resolve(Anonymous(), IPAddress.Parse("203.0.113.7"));

        caller.Partition.Should().Be("ip:203.0.113.7");
        caller.AgencyPartition.Should().BeNull();
    }

    [Fact]
    public void Two_signed_in_users_behind_one_office_address_are_counted_separately()
    {
        // The case an address-only limit gets wrong: one colleague's mistakes locking out the office.
        var office = IPAddress.Parse("203.0.113.7");
        var ada = Guid.CreateVersion7();
        var bayo = Guid.CreateVersion7();

        var first = RateLimitPartitioner.Resolve(SignedIn(ada, AgencyId), office);
        var second = RateLimitPartitioner.Resolve(SignedIn(bayo, AgencyId), office);

        first.Partition.Should().Be($"user:{ada:N}");
        second.Partition.Should().Be($"user:{bayo:N}");
        first.Partition.Should().NotBe(second.Partition);
    }

    [Fact]
    public void A_signed_in_user_also_counts_towards_their_agency()
    {
        var caller = RateLimitPartitioner.Resolve(SignedIn(Guid.CreateVersion7(), AgencyId), IPAddress.Loopback);

        caller.AgencyPartition.Should().Be($"agency:{AgencyId:N}");
    }

    [Fact]
    public void Platform_staff_belong_to_no_agency_and_so_to_no_agency_bucket()
    {
        var staff = Guid.CreateVersion7();

        var caller = RateLimitPartitioner.Resolve(SignedIn(staff, agencyId: null), IPAddress.Loopback);

        caller.Partition.Should().Be($"user:{staff:N}");
        caller.AgencyPartition.Should().BeNull();
    }

    [Fact]
    public void Claims_on_an_unauthenticated_principal_are_not_believed()
    {
        // A principal is only a signed-in user once authentication has vouched for it. Claims on an
        // unauthenticated identity came from nowhere in particular.
        var claims = new ClaimsIdentity([new Claim(TripsClaimTypes.Subject, Guid.CreateVersion7().ToString())]);

        var caller = RateLimitPartitioner.Resolve(new ClaimsPrincipal(claims), IPAddress.Parse("198.51.100.4"));

        caller.Partition.Should().Be("ip:198.51.100.4");
    }

    [Fact]
    public void An_IPv4_address_arriving_as_mapped_IPv6_shares_a_count_with_itself()
    {
        var plain = RateLimitPartitioner.AddressPartition(IPAddress.Parse("203.0.113.7"));
        var mapped = RateLimitPartitioner.AddressPartition(IPAddress.Parse("::ffff:203.0.113.7"));

        mapped.Should().Be(plain);
    }

    [Fact]
    public void IPv6_callers_are_counted_by_their_slash_64_so_rotating_addresses_does_not_escape_the_limit()
    {
        var one = RateLimitPartitioner.AddressPartition(IPAddress.Parse("2001:db8:1:2::1"));
        var sameHousehold = RateLimitPartitioner.AddressPartition(IPAddress.Parse("2001:db8:1:2:ffff:eeee:dddd:9"));
        var neighbour = RateLimitPartitioner.AddressPartition(IPAddress.Parse("2001:db8:1:3::1"));

        one.Should().Be("ip:2001:db8:1:2::/64");
        sameHousehold.Should().Be(one);
        neighbour.Should().NotBe(one);
    }

    [Fact]
    public void A_request_with_no_address_shares_one_bucket_rather_than_escaping_the_limit()
    {
        RateLimitPartitioner.Resolve(Anonymous(), clientAddress: null).Partition
            .Should().Be(RateLimitPartitioner.UnknownAddress);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn(Guid userId, Guid? agencyId)
    {
        var claims = new List<Claim> { new(TripsClaimTypes.Subject, userId.ToString()) };

        if (agencyId is { } agency)
        {
            claims.Add(new Claim(TripsClaimTypes.AgencyId, agency.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }
}
