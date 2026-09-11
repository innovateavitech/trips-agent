using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using TripsAgent.Application.Identity;

namespace TripsAgent.Application.RateLimiting;

/// <summary>Whose count a request is added to.</summary>
/// <param name="Partition">
/// <c>user:{id}</c> for a signed-in caller, <c>ip:{address}</c> for an anonymous one.
/// </param>
/// <param name="AgencyPartition">
/// <c>agency:{id}</c> when the caller acts for an agency, so its traffic also counts towards the
/// agency's ceiling. Null for anonymous callers and for platform staff, who belong to no agency.
/// </param>
public sealed record RateLimitCaller(string Partition, string? AgencyPartition);

/// <summary>
/// Decides whose count a request is added to: the user once they are signed in, the client address
/// until then, and the agency on top of the user.
/// </summary>
/// <remarks>
/// <para>
/// Why all three. An address alone punishes a whole office behind one NAT address for one person's
/// mistakes. A user alone does nothing to an attacker who has not signed in. And an agency ceiling
/// stops one agency's traffic from degrading everybody else's.
/// </para>
/// <para>
/// The address has to be the <i>client's</i>, which behind a load balancer is only true once the
/// forwarded-headers middleware has run with a known-proxy allowlist. That is configured in the API;
/// this only reads what it is given.
/// </para>
/// </remarks>
public static class RateLimitPartitioner
{
    /// <summary>
    /// The partition for a request with no client address. Real sockets always have one; this is a
    /// single shared bucket rather than no limit, so that nothing can get round the limiter by
    /// arriving without an address.
    /// </summary>
    public const string UnknownAddress = "ip:unknown";

    public static RateLimitCaller Resolve(ClaimsPrincipal? user, IPAddress? clientAddress)
    {
        if (user?.Identity?.IsAuthenticated == true && ReadGuid(user, TripsClaimTypes.Subject) is { } userId)
        {
            var agencyId = ReadGuid(user, TripsClaimTypes.AgencyId);

            return new RateLimitCaller(
                $"user:{userId:N}",
                agencyId is { } agency ? $"agency:{agency:N}" : null);
        }

        return new RateLimitCaller(AddressPartition(clientAddress), AgencyPartition: null);
    }

    /// <summary>The partition for an anonymous caller at <paramref name="address"/>.</summary>
    /// <remarks>
    /// An IPv6 caller is counted by its <c>/64</c>, not its full address. A single home or office
    /// connection is normally handed a whole /64 — eighteen quintillion addresses — so counting each
    /// address separately would let anyone walk past the limit by picking a new one per request.
    /// </remarks>
    public static string AddressPartition(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownAddress;
        }

        // An IPv4 client reaching a dual-stack socket shows up as ::ffff:a.b.c.d. It is still one
        // IPv4 address, and must share a count with itself however it arrived.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);

            return $"ip:{new IPAddress(bytes)}/64";
        }

        return $"ip:{address}";
    }

    private static Guid? ReadGuid(ClaimsPrincipal user, string claimType) =>
        Guid.TryParse(user.FindFirst(claimType)?.Value, out var value) ? value : null;
}
