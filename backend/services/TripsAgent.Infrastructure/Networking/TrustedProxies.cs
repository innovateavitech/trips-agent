using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace TripsAgent.Infrastructure.Networking;

/// <summary>
/// The proxies whose <c>X-Forwarded-For</c> header the API believes.
/// </summary>
/// <param name="KnownProxies">Single addresses, e.g. the load balancer's.</param>
/// <param name="KnownNetworks">Ranges in CIDR form, e.g. a VPC subnet or a CDN's published ranges.</param>
/// <param name="ForwardLimit">
/// How many proxies deep to trust. 1 for a single load balancer; 2 for a CDN in front of a load
/// balancer, with both listed above.
/// </param>
public sealed record TrustedProxySettings(
    IReadOnlyList<IPAddress> KnownProxies,
    IReadOnlyList<IPNetwork> KnownNetworks,
    int ForwardLimit);

/// <summary>
/// Reads the known-proxy allowlist for the forwarded-headers middleware.
/// </summary>
/// <remarks>
/// <para>
/// Behind a load balancer every request's socket address is the load balancer's, and the client's
/// real address is in <c>X-Forwarded-For</c>. But that header is just text the client sent, and
/// anyone can send it. Believing it from anybody would let a caller name a fresh address on every
/// request and walk straight past a per-address rate limit. So it is believed only when the request
/// actually arrived from an address on this list.
/// </para>
/// <para>
/// Empty is the safe default: only loopback is trusted (the framework's own default), so the
/// header is ignored and the socket address is used. That is right for local development and
/// wrong behind a load balancer, where every client would then share the balancer's one address —
/// so a deployment behind one must list it.
/// </para>
/// </remarks>
public static class TrustedProxies
{
    /// <summary>The configuration section the allowlist is read from.</summary>
    public const string SectionName = "ForwardedHeaders";

    private const int DeepestForwardLimit = 5;

    public static TrustedProxySettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        var proxies = Split(section["KnownProxies"]).Select(ParseAddress).ToList();
        var networks = Split(section["KnownNetworks"]).Select(ParseNetwork).ToList();

        return new TrustedProxySettings(proxies, networks, ReadForwardLimit(section["ForwardLimit"]));
    }

    private static IPAddress ParseAddress(string value)
    {
        // IPAddress.TryParse is generous: it reads "10" as 0.0.0.10 and "10.1" as 10.0.0.1. For an
        // allowlist, a surprising reading is a hole, so an IPv4 address must be written out in full.
        var looksComplete = value.Contains(':', StringComparison.Ordinal) || value.Count(c => c == '.') == 3;

        if (!looksComplete || !IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException(
                $"{SectionName}__KnownProxies: '{value}' is not an IP address. Write each one in full "
                + "(10.0.0.5, not 10.5) and separate them with commas. Ranges go in KnownNetworks.");
        }

        return address;
    }

    private static IPNetwork ParseNetwork(string value)
    {
        // .NET accepts "10.0.0.1/8" and quietly reads it as 10.0.0.0/8. Whoever wrote it probably meant
        // one of the two — a single proxy or the whole range — and in an allowlist the difference is
        // sixteen million addresses. So host bits are refused rather than guessed about.
        var slash = value.IndexOf('/', StringComparison.Ordinal);

        if (!IPNetwork.TryParse(value, out var network)
            || !IPAddress.TryParse(value[..Math.Max(slash, 0)], out var written)
            || HasHostBits(written, network.PrefixLength))
        {
            throw new InvalidOperationException(
                $"{SectionName}__KnownNetworks: '{value}' is not a network in CIDR form. Write it as "
                + "10.0.0.0/8, with no host bits set, and separate several with commas.");
        }

        // A zero-length prefix is every address there is — trusting X-Forwarded-For from anybody,
        // which is exactly the hole this list exists to close.
        if (network.PrefixLength == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}__KnownNetworks: '{value}' trusts every address on the internet, which lets "
                + "any caller choose the IP address the rate limiter sees. List only your own proxies.");
        }

        var tooWide = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork
            ? network.PrefixLength < 8
            : network.PrefixLength < 16;

        if (tooWide)
        {
            throw new InvalidOperationException(
                $"{SectionName}__KnownNetworks: '{value}' is far wider than any proxy fleet. List the "
                + "load balancer's subnet or the CDN's published ranges, not a large part of the internet.");
        }

        return network;
    }

    /// <summary>True when any bit after the first <paramref name="prefixLength"/> is set.</summary>
    private static bool HasHostBits(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();

        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
        {
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadForwardLimit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            || limit < 1
            || limit > DeepestForwardLimit)
        {
            throw new InvalidOperationException(
                $"{SectionName}__ForwardLimit must be a whole number from 1 to {DeepestForwardLimit} — the "
                + $"number of proxies between the internet and the API. It was '{value}'.");
        }

        return limit;
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
