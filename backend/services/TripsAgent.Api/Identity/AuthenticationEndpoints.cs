using Microsoft.EntityFrameworkCore;
using TripsAgent.Api.RateLimiting;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Identity.Authentication;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;

namespace TripsAgent.Api.Identity;

/// <summary>Sign in, refresh, sign out.</summary>
/// <remarks>
/// The access token comes back in the JSON body; the refresh token never does. It travels only in
/// an <c>HttpOnly</c> cookie, so no script on a console page can read it — see
/// <see cref="RefreshTokenCookie"/>.
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>
    /// The one message every failed sign-in gets, whatever actually went wrong.
    /// </summary>
    public const string InvalidCredentialsMessage = "That email address and password do not match.";

    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", async (
                LoginRequest request,
                LoginHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(
                    request,
                    ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    userAgent: http.Request.Headers.UserAgent.ToString(),
                    cancellationToken);

                return outcome switch
                {
                    LoginOutcome.Succeeded success => Results.Ok(Respond(http, success.Tokens)),

                    // 403 rather than 401: the credentials were right, so retrying them will not
                    // help. The client needs to send the user to the verification screen.
                    LoginOutcome.EmailNotVerified => Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Your email address has not been verified.",
                        detail: "Check your inbox for the code, or ask for a new one."),

                    LoginOutcome.AccountUnavailable => Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "This account is not currently available.",
                        detail: "Ask your agency owner or Trips support to restore access."),

                    _ => Results.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: InvalidCredentialsMessage),
                };
            })
            .WithName("Login")

            // Per client address: every attempt is a password guess. See RateLimitSettings.Defaults.
            .RequireRateLimitPolicy(RateLimitPolicyNames.Login)
            .Produces<TokenPairResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // No body: the refresh token is the cookie. A cross-site page cannot make the browser send
        // it (SameSite=Strict), and could not read the answer if it did (CORS).
        group.MapPost("/refresh", async (
                RefreshTokenHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(
                    new RefreshTokenRequest(RefreshTokenCookie.Read(http.Request) ?? string.Empty),
                    ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken);

                if (outcome is RefreshOutcome.Succeeded success)
                {
                    return Results.Ok(Respond(http, success.Tokens));
                }

                // A cookie the server will never accept again is dead weight; clearing it means the
                // next page load does not present it a second time.
                RefreshTokenCookie.Delete(http);

                // Reuse gets the same 401 as any other bad token, or a missing one. Saying "we
                // detected reuse" would tell an attacker their stolen token had been noticed.
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "That session has expired.",
                    detail: "Sign in again to continue.");
            })
            .WithName("RefreshToken")
            .Produces<TokenPairResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", async (
                LogoutHandler handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(
                    new RefreshTokenRequest(RefreshTokenCookie.Read(http.Request) ?? string.Empty),
                    cancellationToken);

                RefreshTokenCookie.Delete(http);

                // Always 204, whatever the token was — see LogoutHandler.
                return Results.NoContent();
            })
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/me", async (
                ITenantContext tenant,
                IAppDbContext db,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                if (tenant.UserId is not { } userId)
                {
                    return Results.Unauthorized();
                }

                // Read through the tenant filter on purpose. The agency comes back only because
                // the token's agency_id claim populated ITenantContext — which makes this
                // endpoint a live check that the whole chain is connected.
                var agency = tenant.AgencyId is null
                    ? null
                    : await db.Agencies
                        .Where(a => a.Id == tenant.AgencyId)
                        .Select(a => a.TradingName ?? a.LegalName)
                        .FirstOrDefaultAsync(cancellationToken);

                var roles = http.User.FindAll(TripsClaimTypes.Role).Select(claim => claim.Value).ToList();
                var email = http.User.FindFirst(TripsClaimTypes.Email)?.Value ?? string.Empty;

                return Results.Ok(new CurrentUserResponse(userId, email, tenant.AgencyId, agency, roles));
            })
            .WithName("CurrentUser")
            .RequireAuthorization()
            .Produces<CurrentUserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>Sets the refresh cookie and returns the access token.</summary>
    private static TokenPairResponse Respond(HttpContext http, IssuedTokenPair pair)
    {
        RefreshTokenCookie.Write(http, pair.RefreshTokenValue, pair.Stored.ExpiresAt);

        return new(
            pair.Access.Value,
            (int)Math.Round((pair.Access.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, MidpointRounding.AwayFromZero));
    }
}
