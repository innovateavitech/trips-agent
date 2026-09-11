using System.Globalization;
using System.Threading.RateLimiting;
using TripsAgent.Application.RateLimiting;

namespace TripsAgent.Api.RateLimiting;

/// <summary>
/// The limiter the ASP.NET Core rate limiting middleware asks, counting in Redis so that every API
/// instance shares one count.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own limiters (fixed window, token bucket, …) keep their counts in the process's
/// memory. Behind a load balancer that means each instance enforces the limit separately, and with
/// two instances a caller gets twice the limit — while every instance believes it is enforcing the
/// real one. This keeps the middleware and swaps only the counting.
/// </para>
/// <para>
/// <b>Why <see cref="AttemptAcquireCore"/> always declines.</b> The middleware first asks
/// synchronously, and only if that is refused does it call <see cref="AcquireAsyncCore"/> (see
/// <c>RateLimitingMiddleware.TryAcquireAsync</c> in ASP.NET Core). Redis can only be asked
/// asynchronously — a synchronous call would block a thread for a network round trip on every
/// request — so the synchronous answer is "not yet", which counts nothing, and the real decision is
/// made once, asynchronously. The integration tests fail if a framework upgrade ever changes that.
/// </para>
/// </remarks>
internal sealed class SharedRateLimiter : PartitionedRateLimiter<HttpContext>
{
    private readonly RateLimitEvaluator _evaluator;

    public SharedRateLimiter(RateLimitEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
    }

    /// <summary>A retry-after, in whole seconds, never zero — a client told "0" retries at once.</summary>
    internal static int WholeSeconds(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));

    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;

    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) =>
        UndecidedLease.Instance;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        HttpContext resource,
        int permitCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var policy = resource.GetEndpoint()?.Metadata.GetMetadata<RateLimitPolicyMetadata>()?.PolicyName
                     ?? RateLimitPolicyNames.Default;

        // RemoteIpAddress is the client's here, not the load balancer's, because UseForwardedHeaders
        // has already run — and it only rewrites it for requests that came through a known proxy.
        var caller = RateLimitPartitioner.Resolve(resource.User, resource.Connection.RemoteIpAddress);

        var decision = await _evaluator.EvaluateAsync(policy, caller, cancellationToken);

        if (decision.Counted)
        {
            WriteHeaders(resource.Response.Headers, decision);
        }

        return new DecisionLease(decision);
    }

    /// <summary>
    /// The <c>RateLimit-*</c> headers, on every counted response — not only on a 429 — so a
    /// well-behaved client can slow down before it is refused.
    /// </summary>
    private static void WriteHeaders(IHeaderDictionary headers, RateLimitDecision decision)
    {
        headers[RateLimitHeaders.Limit] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        headers[RateLimitHeaders.Remaining] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        headers[RateLimitHeaders.Reset] = WholeSeconds(decision.ResetsIn).ToString(CultureInfo.InvariantCulture);
        headers[RateLimitHeaders.Policy] = string.Create(
            CultureInfo.InvariantCulture, $"{decision.Limit};w={WholeSeconds(decision.Window)}");
    }

    /// <summary>The verdict, carried to the middleware — and, when it is a refusal, to OnRejected.</summary>
    internal sealed class DecisionLease : RateLimitLease
    {
        public DecisionLease(RateLimitDecision decision) => Decision = decision;

        public RateLimitDecision Decision { get; }

        public override bool IsAcquired => Decision.Allowed;

        public override IEnumerable<string> MetadataNames =>
            Decision.Allowed ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (!Decision.Allowed && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = Decision.ResetsIn;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    /// <summary>The synchronous "not yet" — see the class remarks. Holds nothing, so there is nothing to release.</summary>
    private sealed class UndecidedLease : RateLimitLease
    {
        public static readonly UndecidedLease Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
