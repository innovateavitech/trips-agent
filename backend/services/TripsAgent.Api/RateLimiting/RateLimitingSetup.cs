using System.Globalization;
using Microsoft.AspNetCore.RateLimiting;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Infrastructure.RateLimiting;

namespace TripsAgent.Api.RateLimiting;

/// <summary>The response headers the limiter writes. Public so tests and clients agree on the spelling.</summary>
/// <remarks>
/// The IETF <c>RateLimit</c> header draft's names. <c>Reset</c> is seconds from now, not a timestamp,
/// so a client with a wrong clock still reads it correctly.
/// </remarks>
public static class RateLimitHeaders
{
    public const string Limit = "RateLimit-Limit";
    public const string Remaining = "RateLimit-Remaining";
    public const string Reset = "RateLimit-Reset";
    public const string Policy = "RateLimit-Policy";
}

/// <summary>Names the rate limit policy an endpoint is counted under. See <see cref="RateLimitingSetup.RequireRateLimitPolicy"/>.</summary>
public sealed record RateLimitPolicyMetadata(string PolicyName);

/// <summary>
/// Rate limiting for the API (issue #102): per client address until sign-in, per user after it, and
/// per agency on top — counted in Redis, so the limit is the same however many instances are running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it sits.</b> After <c>UseForwardedHeaders</c>, so the address it sees is the client's and
/// not the load balancer's; after <c>UseAuthentication</c>, so a signed-in caller is counted as
/// themselves rather than as their office's shared address.
/// </para>
/// <para>
/// <b>With no Redis.</b> Switched on and with no Redis configured, the API refuses to start. There is
/// no in-memory fallback on purpose: it would look right on one machine and quietly double every limit
/// with two instances. Local development has rate limiting off (appsettings.Development.json), so the
/// test suite and a developer hammering the sign-in screen are not throttled by one another.
/// </para>
/// <para>
/// <b>When Redis goes down while running,</b> requests are let through uncounted and the outage is
/// logged — see <see cref="RateLimitEvaluator"/>.
/// </para>
/// </remarks>
public static partial class RateLimitingSetup
{
    /// <summary>The logger category rejections are written under.</summary>
    public const string LogCategory = "TripsAgent.Api.RateLimiting";

    public static IServiceCollection AddSharedRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = RateLimitingRegistration.ReadSettings(configuration);
        services.AddSingleton(settings);

        if (!settings.Enabled)
        {
            return services;
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = RejectAsync;
        });

        // The limiter needs the store from the container, which AddRateLimiter's own callback cannot
        // reach. This runs when the middleware is built — at startup, not at the first request, and
        // not at all for the migrate and seed commands, which never build it.
        services.AddOptions<RateLimiterOptions>().Configure<IServiceProvider>((options, provider) =>
        {
            var store = provider.GetService<IRateLimitStore>() ?? throw new InvalidOperationException(
                """
                Rate limiting is switched on, but no Redis is configured to keep the counts in.

                Set ConnectionStrings__Redis. Counting in each process's memory instead is not offered:
                with two API instances every limit would silently become twice what it says.

                To run without rate limiting — a local tool, never a deployed API — set
                RateLimiting__Enabled=false explicitly.
                """);

            options.GlobalLimiter = new SharedRateLimiter(new RateLimitEvaluator(store, settings));
        });

        return services;
    }

    /// <summary>Adds the middleware, unless rate limiting is switched off.</summary>
    public static IApplicationBuilder UseSharedRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.ApplicationServices.GetRequiredService<RateLimitSettings>();

        if (!settings.Enabled)
        {
            var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
            var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

            // Expected in Development; anywhere else it is a decision somebody should see.
            if (environment.IsDevelopment())
            {
                LogDisabledInDevelopment(logger);
            }
            else
            {
                LogDisabled(logger, environment.EnvironmentName);
            }

            return app;
        }

        return app.UseRateLimiter();
    }

    /// <summary>
    /// Counts this endpoint under <paramref name="policyName"/> instead of the default policy.
    /// </summary>
    /// <remarks>
    /// Named policies are for the requests that cost money or leak information when repeated: sign-in,
    /// registration, the email-sending endpoints and search. An unknown name fails at startup rather
    /// than quietly falling back to the default.
    /// </remarks>
    public static TBuilder RequireRateLimitPolicy<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!RateLimitPolicyNames.IsEndpointPolicy(policyName))
        {
            throw new ArgumentException(
                $"'{policyName}' is not a rate limit policy an endpoint can use. Use one of the names in "
                + $"{nameof(RateLimitPolicyNames)} other than {RateLimitPolicyNames.Agency}.",
                nameof(policyName));
        }

        return builder.WithMetadata(new RateLimitPolicyMetadata(policyName));
    }

    /// <summary>
    /// 429 with <c>Retry-After</c> and a problem body, and a log line naming the partition — an IP
    /// address, a user or an agency — so abuse shows up in the logs instead of passing silently.
    /// </summary>
    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfterSeconds = 1;

        if (context.Lease is SharedRateLimiter.DecisionLease { Decision: var decision })
        {
            retryAfterSeconds = SharedRateLimiter.WholeSeconds(decision.ResetsIn);

            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
            LogRejected(
                logger,
                decision.Policy,
                decision.Partition,
                http.Request.Path,
                decision.Limit,
                decision.Window,
                retryAfterSeconds);
        }

        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests.",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"Wait {retryAfterSeconds} second{(retryAfterSeconds == 1 ? string.Empty : "s")} and try again."))
            .ExecuteAsync(http);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rate limit {Policy} refused {Partition} on {Path}: more than {Limit} requests in {Window}. "
                  + "Retry after {RetryAfterSeconds}s.")]
    private static partial void LogRejected(
        ILogger logger,
        string policy,
        string partition,
        string path,
        int limit,
        TimeSpan window,
        int retryAfterSeconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rate limiting is switched off in {Environment} (RateLimiting__Enabled=false). Nothing limits "
                  + "sign-in attempts, registrations or search per caller.")]
    private static partial void LogDisabled(ILogger logger, string environment);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rate limiting is off in Development. Set RateLimiting__Enabled=true, with Redis running, to try it.")]
    private static partial void LogDisabledInDevelopment(ILogger logger);
}
