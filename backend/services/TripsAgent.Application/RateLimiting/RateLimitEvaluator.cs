namespace TripsAgent.Application.RateLimiting;

/// <summary>The verdict on one request, and the numbers the response headers report.</summary>
public sealed record RateLimitDecision
{
    /// <summary>False when the request is over a limit and must be refused with a 429.</summary>
    public required bool Allowed { get; init; }

    /// <summary>
    /// False when the store could not be reached, so nothing was counted and the request was let
    /// through. The other numbers are meaningless then, and no headers are sent.
    /// </summary>
    public bool Counted { get; init; } = true;

    /// <summary>The policy whose numbers these are — the one that refused, or the one nearest its limit.</summary>
    public required string Policy { get; init; }

    /// <summary>The partition key the count belongs to: <c>ip:…</c>, <c>user:…</c> or <c>agency:…</c>.</summary>
    public required string Partition { get; init; }

    public int Limit { get; init; }

    public int Remaining { get; init; }

    /// <summary>How long until this window ends and the count starts again.</summary>
    public TimeSpan ResetsIn { get; init; }

    public TimeSpan Window { get; init; }
}

/// <summary>
/// Counts a request against every bucket it belongs to, and decides whether it may proceed.
/// </summary>
/// <remarks>
/// A signed-in request belongs to two buckets — its user's, under the endpoint's policy, and its
/// agency's — and must be inside both. Both are counted on every request, including one that ends
/// up refused: a refused request still reached us, and not counting it would let a caller hammer a
/// limit for free.
/// </remarks>
public sealed class RateLimitEvaluator
{
    /// <summary>Every rate limit key starts with this, so they are easy to find, and to leave alone.</summary>
    public const string KeyPrefix = "ratelimit:";

    private readonly IRateLimitStore _store;
    private readonly RateLimitSettings _settings;

    public RateLimitEvaluator(IRateLimitStore store, RateLimitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        _store = store;
        _settings = settings;
    }

    /// <summary>The store key for <paramref name="partition"/> under <paramref name="policyName"/>.</summary>
    public static string KeyFor(string policyName, string partition) => $"{KeyPrefix}{policyName}:{partition}";

    public async Task<RateLimitDecision> EvaluateAsync(
        string policyName,
        RateLimitCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var buckets = new List<Bucket> { new(policyName, caller.Partition, _settings.RuleFor(policyName)) };

        if (caller.AgencyPartition is { } agency)
        {
            buckets.Add(new Bucket(RateLimitPolicyNames.Agency, agency, _settings.RuleFor(RateLimitPolicyNames.Agency)));
        }

        // Both at once: they are independent, and a signed-in request should not pay two round trips.
        var counts = await Task.WhenAll(buckets.Select(bucket =>
            _store.HitAsync(KeyFor(bucket.Policy, bucket.Partition), bucket.Rule.Window, cancellationToken)));

        // The store is unreachable: let the request through, uncounted. Refusing everything instead
        // would turn a Redis outage into an outage of the whole API, sign-in included — and the
        // limiter is a guard against abuse, not something correctness depends on. Account lockout
        // (#15) still stands behind sign-in either way. The store logs the outage.
        if (counts.Any(count => count is null))
        {
            return new RateLimitDecision
            {
                Allowed = true,
                Counted = false,
                Policy = policyName,
                Partition = caller.Partition,
            };
        }

        var decisions = buckets.Zip(counts, (bucket, count) => Decide(bucket, count!.Value)).ToList();

        // Report a refusal if there is one — the longest wait, since that is when the caller can
        // actually proceed. Otherwise the bucket nearest its limit, which is the one to slow down for.
        return decisions.Where(decision => !decision.Allowed).OrderByDescending(decision => decision.ResetsIn).FirstOrDefault()
               ?? decisions.OrderBy(decision => decision.Remaining).ThenByDescending(decision => decision.ResetsIn).First();
    }

    private static RateLimitDecision Decide(Bucket bucket, RateLimitWindowCount count) =>
        new()
        {
            Allowed = count.Hits <= bucket.Rule.PermitLimit,
            Policy = bucket.Policy,
            Partition = bucket.Partition,
            Limit = bucket.Rule.PermitLimit,
            Remaining = (int)Math.Max(0, bucket.Rule.PermitLimit - count.Hits),
            ResetsIn = count.ResetsIn < TimeSpan.Zero ? TimeSpan.Zero : count.ResetsIn,
            Window = bucket.Rule.Window,
        };

    private sealed record Bucket(string Policy, string Partition, RateLimitRule Rule);
}
