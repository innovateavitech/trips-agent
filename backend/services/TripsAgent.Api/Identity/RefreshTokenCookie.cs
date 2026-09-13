namespace TripsAgent.Api.Identity;

/// <summary>
/// Where the refresh token travels between the API and a console: an <c>HttpOnly</c> cookie the
/// page's own script can never read (issue 107).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cookie.</b> The refresh token lives thirty days. Kept in <c>sessionStorage</c>, any
/// script that ever runs on a console page — one bad dependency, one XSS hole — could copy it out
/// and use it from anywhere for a month. In an <c>HttpOnly</c> cookie the browser sends it, and
/// nothing on the page can read it. The access token stays in the console's memory: it lives fifteen
/// minutes and is never written anywhere.
/// </para>
/// <para>
/// <b>The flags.</b> <c>Secure</c>, so it never crosses plain HTTP (browsers treat
/// <c>http://localhost</c> as secure, which is what keeps local development working).
/// <c>SameSite=Strict</c>, so no other site — including an agency's storefront — can make a browser
/// send it; that is also why the refresh and sign-out calls need no CSRF token. <c>Path</c> is the
/// authentication routes only, so it is not attached to every other API call.
/// </para>
/// <para>
/// <b>One cookie per console.</b> Cookies belong to a host, not a port, so on a developer's machine
/// the agent console (5173) and the admin console (5174) share one cookie jar. With a single cookie,
/// signing in to one console would silently replace the other's session. Each console therefore says
/// which it is in the <see cref="ClientHeader"/> header, and gets a cookie of its own.
/// </para>
/// </remarks>
public static class RefreshTokenCookie
{
    /// <summary>The header a console names itself in: <c>agent</c> (the default) or <c>admin</c>.</summary>
    public const string ClientHeader = "X-Session-Client";

    /// <summary>The agent console's cookie, and the one used when no client is named.</summary>
    public const string AgentCookieName = "refresh_token";

    /// <summary>The admin console's cookie.</summary>
    public const string AdminCookieName = "admin_refresh_token";

    /// <summary>The only routes the cookie is sent to.</summary>
    public const string CookiePath = "/api/v1/auth";

    /// <summary>The cookie this request's console uses.</summary>
    public static string NameFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(request.Headers[ClientHeader].ToString(), "admin", StringComparison.OrdinalIgnoreCase)
            ? AdminCookieName
            : AgentCookieName;
    }

    /// <summary>The refresh token the browser sent, or null when there is none.</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(NameFor(request), out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    /// <summary>Stores a freshly issued refresh token, replacing the one it rotated out.</summary>
    public static void Write(HttpContext http, string refreshToken, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.Cookies.Append(NameFor(http.Request), refreshToken, Options(expiresAt));
    }

    /// <summary>
    /// Tells the browser to drop the cookie. The flags have to match the ones it was set with, or
    /// the browser treats it as a different cookie and keeps the old one.
    /// </summary>
    public static void Delete(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.Cookies.Delete(NameFor(http.Request), Options(expiresAt: null));
    }

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expiresAt,
        IsEssential = true,
    };
}
