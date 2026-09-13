using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>Starts each sub-agent's allowance period again when it is due.</summary>
public interface IAllowanceResetJob
{
    /// <summary>Resets every allowance whose period has turned over. Returns how many it reset.</summary>
    public Task<int> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The clock behind a periodic allowance: at the turn of the day, week or month, what a sub-agent
/// has spent goes back to zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> Each row carries the instant it is next due, and resetting moves that
/// forward past now. Running the job twice in one period therefore resets once — the second pass
/// finds nothing due. That matters because an operator will trigger it by hand from the Hangfire
/// dashboard while investigating, and because a redeploy can run it twice in a minute.
/// </para>
/// <para>
/// <b>It reads and writes every agency's rows</b>, which is precisely what
/// <see cref="IPlatformScope"/> is for: one audited, explained scope rather than a filter switched
/// off. A per-agency job would need a schedule per agency.
/// </para>
/// <para>
/// Lifetime allowances have no reset instant and are never touched. Changing one means changing
/// its cap.
/// </para>
/// </remarks>
public sealed partial class AllowanceResetJob : IAllowanceResetJob
{
    /// <summary>How many are reset in one pass, so a long backlog does not hold one transaction open.</summary>
    private const int BatchSize = 500;

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<AllowanceResetJob> _logger;

    public AllowanceResetJob(
        IAppDbContext db,
        IPlatformScope platformScope,
        TimeProvider clock,
        ILogger<AllowanceResetJob> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "sub-agent allowance reset — every agency's allowance period turns over on the same schedule");

        var now = _clock.GetUtcNow();
        var reset = 0;

        while (true)
        {
            var due = await _db.WalletAllowances
                .Where(allowance => allowance.ResetsAt != null && allowance.ResetsAt <= now)
                .OrderBy(allowance => allowance.ResetsAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (due.Count == 0)
            {
                break;
            }

            // The entity decides whether it is really due and where the next boundary is, so the
            // rule lives in one place and is unit-tested without a database.
            reset += due.Count(allowance => allowance.ResetIfDue(now));

            await _db.SaveChangesAsync(cancellationToken);

            if (due.Count < BatchSize)
            {
                break;
            }
        }

        if (reset > 0)
        {
            LogReset(_logger, reset);
        }

        return reset;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Started a new allowance period for {Count} sub-agents.")]
    private static partial void LogReset(ILogger logger, int count);
}
