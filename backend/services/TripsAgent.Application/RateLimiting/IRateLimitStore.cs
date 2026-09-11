namespace TripsAgent.Application.RateLimiting;

/// <summary>
/// Where request counts live. Shared by every API instance, which is the whole point: a count kept
/// in one process's memory only knows about that process, so with two instances behind a load
/// balancer every limit would quietly be twice what it says.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Counts one request against <paramref name="key"/>. The first request starts a window of
    /// <paramref name="window"/>; when it ends, the count starts again from zero.
    /// </summary>
    /// <returns>
    /// The count so far in the current window, or <c>null</c> when the store cannot be reached.
    /// Null is an answer, not an error: the caller decides what an unreachable store means.
    /// </returns>
    public Task<RateLimitWindowCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>How many requests the current window has seen, and how long until it ends.</summary>
public readonly record struct RateLimitWindowCount(long Hits, TimeSpan ResetsIn);
