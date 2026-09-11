using Microsoft.AspNetCore.HttpOverrides;
using TripsAgent.Infrastructure.Networking;

namespace TripsAgent.Api.Networking;

/// <summary>
/// Makes <c>HttpContext.Connection.RemoteIpAddress</c> the client's address rather than the load
/// balancer's — but only for requests that really came through a proxy we run.
/// </summary>
/// <remarks>
/// <para>
/// The rate limiter, the login-attempt log and the audit log all read the client's address. Behind a
/// load balancer the socket address is the balancer's, and the client's is in <c>X-Forwarded-For</c>.
/// Anyone can send that header, though, so it is believed only from the addresses in
/// <c>ForwardedHeaders__KnownProxies</c> and <c>ForwardedHeaders__KnownNetworks</c>. From anywhere
/// else it is ignored and the socket address stands. See <see cref="TrustedProxies"/>.
/// </para>
/// <para>
/// The storefront's server-side rendering calls the API from its own server, so every traveller would
/// otherwise share that server's one address. When the storefront goes live, its servers belong on
/// this list, and it must forward the visitor's address.
/// </para>
/// </remarks>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read now rather than inside the callback, so a malformed list stops startup immediately.
        var trusted = TrustedProxies.Read(configuration);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = trusted.ForwardLimit;

            // Added to the framework's defaults, which trust loopback only: a proxy on the same
            // machine is not something a remote caller can pretend to be.
            foreach (var proxy in trusted.KnownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in trusted.KnownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        return services;
    }
}
