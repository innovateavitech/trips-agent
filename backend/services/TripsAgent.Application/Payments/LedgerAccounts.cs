using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Payments;

/// <summary>
/// Finds the ledger accounts a posting needs — opening an agency's wallet account the first time, and
/// never inventing one of the platform's.
/// </summary>
/// <remarks>
/// The same rules <c>WalletTopUpService</c> follows: the agency's wallet account may be opened on first
/// use (a unique index makes a race harmless), while the platform's own accounts are seeded by
/// migration and a missing one is an error, because two of them would split the platform's balance.
/// Callers posting to platform accounts do so inside an <c>IPlatformScope</c>.
/// </remarks>
public sealed class LedgerAccounts
{
    private readonly IAppDbContext _db;

    public LedgerAccounts(IAppDbContext db) => _db = db;

    /// <summary>The agency's wallet account, opened the first time it is needed.</summary>
    public async Task<LedgerAccount> AgencyWalletAsync(Guid agencyId, string currency, CancellationToken cancellationToken = default)
    {
        var existing = await _db.LedgerAccounts.FirstOrDefaultAsync(
            account => account.AgencyId == agencyId && account.AccountType == LedgerAccountType.AgencyWallet && account.Currency == currency,
            cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = LedgerAccount.ForAgency(
            agencyId, LedgerAccountType.AgencyWallet, currency, $"{LedgerAccountType.AgencyWallet} ({currency})");

        _db.LedgerAccounts.Add(created);

        return created;
    }

    /// <summary>One of the platform's own accounts. Never created here.</summary>
    public async Task<LedgerAccount> PlatformAsync(LedgerAccountType accountType, string currency, CancellationToken cancellationToken = default) =>
        await _db.LedgerAccounts.FirstOrDefaultAsync(
            account => account.AgencyId == null && account.AccountType == accountType && account.Currency == currency,
            cancellationToken)
        ?? throw new InvalidOperationException(
            $"The platform has no {accountType} account in {currency}. Platform accounts are seeded by migration; "
            + "add one for this currency before selling in it.");
}
