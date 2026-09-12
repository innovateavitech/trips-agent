namespace TripsAgent.Application.Concurrency;

/// <summary>
/// A lock shared by every process, held for a limited time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defence in depth, never the only defence.</b> A distributed lock can be lost: its lease can run
/// out under a stalled process, and the store behind it can restart. So nothing may rely on it alone.
/// Ticket issuance, its main user, is also guarded by a row lock, by the booking's own state and by a
/// unique index; the lock's job is to turn away a duplicate before it even reaches the database.
/// </para>
/// <para>
/// A port, so the store is a decision Infrastructure makes. Redis today.
/// </para>
/// </remarks>
public interface IDistributedLock
{
    /// <summary>Takes the lock named <paramref name="key"/> for at most <paramref name="lease"/>.</summary>
    /// <returns>A handle that releases the lock when disposed, or null when someone else holds it.</returns>
    public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken = default);
}
