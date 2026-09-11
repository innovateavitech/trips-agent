using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Payments;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Payments;

/// <summary>
/// The integrity queries, as set-based SQL.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL on purpose. These are aggregate questions about every row in the ledger, and LINQ
/// would either produce something unrecognisable or quietly pull the entries into memory to do
/// the grouping — which at 100k+ entries is the difference between a second and a minute.
/// </para>
/// <para>
/// No tenant predicate appears in any of them, because the platform's books span every agency
/// and that is the point of the audit. The caller enters an audited <c>IPlatformScope</c> before
/// asking; see <see cref="LedgerIntegrityAudit"/>.
/// </para>
/// </remarks>
public sealed class LedgerIntegrityQueries : ILedgerIntegrityQueries
{
    // The column aliases below are snake_case rather than matching the record property names.
    // SqlQuery<T> resolves columns through the context's naming convention, and this context uses
    // UseSnakeCaseNamingConvention — so it looks for debits_minor, not DebitsMinor. Aliasing to
    // the property name gives "the required column 'debits_minor' was not present".

    private readonly AppDbContext _db;

    public LedgerIntegrityQueries(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<UnbalancedTransactionGroup>> UnbalancedTransactionGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<UnbalancedTransactionGroup>(
            $"""
             SELECT transaction_group_id AS group_id,
                    SUM(CASE WHEN direction = 'Debit'  THEN amount_minor ELSE 0 END) AS debits_minor,
                    SUM(CASE WHEN direction = 'Credit' THEN amount_minor ELSE 0 END) AS credits_minor
             FROM payments.ledger_entries
             GROUP BY transaction_group_id
             HAVING SUM(CASE WHEN direction = 'Debit'  THEN amount_minor ELSE 0 END)
                 <> SUM(CASE WHEN direction = 'Credit' THEN amount_minor ELSE 0 END)
             ORDER BY transaction_group_id
             """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WalletBalanceDrift>> WalletBalanceDriftAsync(
        CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<WalletBalanceDrift>(
            $"""
             WITH account_balances AS (
                 SELECT account_id,
                        SUM(CASE WHEN direction = 'Credit' THEN amount_minor ELSE 0 END)
                      - SUM(CASE WHEN direction = 'Debit'  THEN amount_minor ELSE 0 END) AS balance_minor
                 FROM payments.ledger_entries
                 GROUP BY account_id
             )
             SELECT w.id            AS wallet_id,
                    w.agency_id     AS agency_id,
                    w.balance_minor AS wallet_balance_minor,
                    COALESCE(b.balance_minor, 0) AS ledger_balance_minor
             FROM payments.wallets w
             LEFT JOIN payments.ledger_accounts a
                    ON a.agency_id = w.agency_id
                   AND a.currency = w.currency
                   AND a.account_type = 'AgencyWallet'
             LEFT JOIN account_balances b ON b.account_id = a.id
             WHERE w.balance_minor <> COALESCE(b.balance_minor, 0)
             ORDER BY w.id
             """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OrphanedLedgerEntry>> OrphanedEntriesAsync(
        CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<OrphanedLedgerEntry>(
            $"""
             SELECT e.id AS entry_id, e.account_id AS account_id, e.amount_minor AS amount_minor
             FROM payments.ledger_entries e
             LEFT JOIN payments.ledger_accounts a ON a.id = e.account_id
             WHERE a.id IS NULL
             ORDER BY e.id
             """).ToListAsync(cancellationToken);
}
