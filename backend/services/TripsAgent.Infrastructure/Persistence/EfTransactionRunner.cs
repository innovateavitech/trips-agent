using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;

namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITransactionRunner"/> over the request's <see cref="AppDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// AddInfrastructure switches on EF Core's retry-on-transient-failure, and that strategy refuses a
/// hand-opened transaction unless the whole unit of work is handed to it, so it can replay the unit
/// from the top. This is that hand-off — the same shape as <c>OutboxDispatcher</c>.
/// </para>
/// <para>
/// A replay clears the change tracker first, so it starts clean rather than re-inserting the
/// failed attempt's half-made rows. The first attempt does not, so anything the caller had already
/// staged is saved along with the work.
/// </para>
/// </remarks>
public sealed class EfTransactionRunner : ITransactionRunner
{
    private readonly AppDbContext _db;

    public EfTransactionRunner(AppDbContext db) => _db = db;

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Already inside the caller's transaction: join it. The caller commits, and a retry, if
        // one is needed, is theirs to run — EF cannot replay half of someone else's transaction.
        if (_db.Database.CurrentTransaction is not null)
        {
            return work(cancellationToken);
        }

        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            async token =>
            {
                if (attempt++ > 0)
                {
                    _db.ChangeTracker.Clear();
                }

                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                var result = await work(token);

                await transaction.CommitAsync(token);

                return result;
            },
            cancellationToken);
    }
}
