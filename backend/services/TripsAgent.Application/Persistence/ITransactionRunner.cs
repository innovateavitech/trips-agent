namespace TripsAgent.Application.Persistence;

/// <summary>
/// Runs a unit of work inside one database transaction: all of it commits, or none of it does.
/// </summary>
/// <remarks>
/// <para>
/// Most use cases never need this — one <c>SaveChangesAsync</c> is already one transaction. It is
/// for work that must <i>read under a lock</i> and then write, where the lock has to be held until
/// the write commits. Taking a gapless document number is the canonical case.
/// </para>
/// <para>
/// If the caller already has a transaction open, the work joins it and the caller decides whether
/// it commits. Otherwise a new transaction is opened and committed when the work returns.
/// </para>
/// <para>
/// <b>The work may run more than once.</b> Production retries a transaction that failed on a
/// transient network error, replaying the whole delegate from the top. So load what it needs
/// inside it, and do nothing in it that cannot be repeated — no email, no supplier call.
/// </para>
/// </remarks>
public interface ITransactionRunner
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}
