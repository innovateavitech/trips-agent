using System.Net;

namespace TripsAgent.Api.Security;

/// <summary>
/// The response headers that stop a browser doing something with an API response that nobody asked
/// for (issue 107).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as the response starts, not as the request arrives.</b> An exception handler clears the
/// response's headers before it writes its error, so headers set on the way in vanish from exactly the
/// responses an attacker provokes on purpose. <c>OnStarting</c> runs last, whatever happened before.
/// </para>
/// <para>
/// <b>No <c>Content-Security-Policy</c> beyond framing.</b> The API answers JSON, and the few things
/// it serves a browser tab directly — a PDF, the Hangfire dashboard — would break under a policy
/// written for JSON. The storefront, which is a web page, has a full policy of its own.
/// </para>
/// <para>
/// <b>Framing is same-origin or a console's origin.</b> The admin console shows KYB documents in an
/// <c>iframe</c>, from this API through the console's own host. Nothing else may frame a response —
/// that is what stops a hostile page overlaying one to trick a click.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>
    /// One year. Only sent over HTTPS, and without <c>includeSubDomains</c> or <c>preload</c>: preload
    /// is close to irreversible, and waits until custom domains are proven in production.
    /// </summary>
    public const string StrictTransportSecurity = "max-age=31536000";

    public const string ReferrerPolicy = "no-referrer";

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    /// <summary>Adds the middleware. First in the pipeline, so every response carries the headers.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IEnumerable<string> frameAncestors)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(frameAncestors);

        var contentSecurityPolicy = string.Join(' ', frameAncestors.Prepend("frame-ancestors 'self'"));

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = ReferrerPolicy;
                headers.XFrameOptions = "SAMEORIGIN";
                headers.ContentSecurityPolicy = contentSecurityPolicy;
                headers["Permissions-Policy"] = PermissionsPolicy;

                // Browsers ignore HSTS over plain HTTP anyway; sending it to localhost over HTTPS
                // would pin a developer's machine to HTTPS for a year.
                if (context.Request.IsHttps && !IsLoopback(context.Request.Host.Host))
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                // A server banner tells a scanner which exploits to try. Kestrel's is switched off
                // in Program.cs; this catches anything else that adds one.
                headers.Remove("Server");
                headers.Remove("X-Powered-By");

                return Task.CompletedTask;
            });

            await next();
        });
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
