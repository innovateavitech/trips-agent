namespace TripsAgent.Application.Payments;

/// <summary>A transaction group whose debits and credits disagree.</summary>
public sealed record UnbalancedTransactionGroup(Guid GroupId, long DebitsMinor, long CreditsMinor);

/// <summary>A wallet whose stored balance disagrees with its ledger account.</summary>
public sealed record WalletBalanceDrift(
    Guid WalletId,
    Guid AgencyId,
    long WalletBalanceMinor,
    long LedgerBalanceMinor);

/// <summary>A ledger entry pointing at an account that does not exist.</summary>
public sealed record OrphanedLedgerEntry(Guid EntryId, Guid AccountId, long AmountMinor);

/// <summary>
/// The aggregate queries behind the nightly integrity audit.
/// </summary>
/// <remarks>
/// <para>
/// A narrow port rather than raw SQL through <c>IAppDbContext</c>, which deliberately does not
/// expose <c>Database</c> — a general "run this SQL" seam on the shared context would be a
/// standing way around the tenant filters, and CLAUDE.md is emphatic about those. This exposes
/// three specific questions instead, and the SQL that answers them lives in Infrastructure where
/// persistence belongs.
/// </para>
/// <para>
/// Every method returns <b>only</b> discrepancies, computed by the database. The budget is 100k+
/// entries; the obvious version that walks each transaction group in C# would take minutes and
/// hold a connection throughout. In a healthy system every one of these returns nothing.
/// </para>
/// </remarks>
public interface ILedgerIntegrityQueries
{
    /// <summary>
    /// Groups where debits do not equal credits.
    /// </summary>
    /// <remarks>
    /// The most important invariant in the system. A result here means money has been created or
    /// destroyed, and every report built on the ledger is wrong.
    /// </remarks>
    public Task<IReadOnlyList<UnbalancedTransactionGroup>> UnbalancedTransactionGroupsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wallets whose <c>balance_minor</c> disagrees with their agency wallet ledger account.
    /// </summary>
    /// <remarks>
    /// The wallet column is a projection kept for fast reads; the ledger is the truth.
    /// </remarks>
    public Task<IReadOnlyList<WalletBalanceDrift>> WalletBalanceDriftAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Entries whose account is missing.
    /// </summary>
    /// <remarks>
    /// A foreign key already forbids this, so the check is really asking whether the foreign key
    /// is still there — a migration that recreated the table without it would otherwise go
    /// unnoticed until a report quietly lost rows to an inner join.
    /// </remarks>
    public Task<IReadOnlyList<OrphanedLedgerEntry>> OrphanedEntriesAsync(
        CancellationToken cancellationToken = default);
}
