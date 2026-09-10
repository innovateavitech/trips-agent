import { useRef, useState } from 'react';
import { Alert, Button, Card, EmptyState, ErrorState } from '@trips/ui';
import type { StatementFilters } from '../types';
import { isLowBalance } from '../top-up-rules';
import { STATEMENT_PAGE_SIZE, useStatement, useWalletSummary } from '../wallet-queries';
import { BalanceCard, BalanceCardSkeleton } from '../components/balance-card';
import { DownloadStatementButton } from '../components/download-statement-button';
import { LowBalanceAlert } from '../components/low-balance-alert';
import { StatementFiltersBar } from '../components/statement-filters-bar';
import { StatementPagination } from '../components/statement-pagination';
import { StatementTable, StatementTableSkeleton } from '../components/statement-table';
import { TopUpPanel } from '../components/top-up-panel';
import { UnfinishedTopUpAlert } from '../components/unfinished-top-up-alert';

const NO_FILTERS: StatementFilters = { types: [], from: null, to: null };

/** FRD 2.5 — balance, top-up, and transaction history, on one screen. */
export function WalletPage() {
  const [filters, setFilters] = useState<StatementFilters>(NO_FILTERS);
  const [page, setPage] = useState(1);
  const topUpRef = useRef<HTMLDivElement>(null);

  const summary = useWalletSummary();
  const statement = useStatement(filters, page);

  const isFiltered = filters.types.length > 0 || filters.from !== null || filters.to !== null;

  function applyFilters(next: StatementFilters) {
    setFilters(next);
    // Back to page one: staying on page 3 of a result set that now has one page
    // shows an empty table and looks like the filter is broken.
    setPage(1);
  }

  return (
    <main className="mx-auto flex w-full max-w-5xl flex-col gap-6 p-4 sm:p-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">Wallet</h1>
        <p className="text-sm text-muted-foreground">
          Your prepaid balance, and every movement in and out of it.
        </p>
      </header>

      <UnfinishedTopUpAlert />

      {summary.isError ? (
        /* A banner rather than the shared <ErrorState> panel: this sits above
           the balance card, where a full-height centred panel would push the
           whole screen down for what is a recoverable read failure. */
        <Alert
          tone="destructive"
          title="We could not load your wallet"
          action={
            <Button size="sm" variant="outline" onClick={() => void summary.refetch()}>
              Try again
            </Button>
          }
        >
          Your balance is safe — this is a problem reading it, not a problem with your money.
        </Alert>
      ) : null}

      {summary.isPending ? <BalanceCardSkeleton /> : null}

      {summary.data ? (
        <>
          {isLowBalance(summary.data) ? (
            <LowBalanceAlert
              summary={summary.data}
              onTopUp={() =>
                topUpRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
              }
            />
          ) : null}

          <BalanceCard summary={summary.data} />

          <div ref={topUpRef}>
            <TopUpPanel summary={summary.data} />
          </div>
        </>
      ) : null}

      <section className="flex flex-col gap-4" aria-labelledby="statement-heading">
        <div className="flex flex-wrap items-end justify-between gap-3">
          <h2
            id="statement-heading"
            className="text-lg font-semibold tracking-tight text-foreground"
          >
            Statement
          </h2>
          {summary.data ? (
            <DownloadStatementButton
              filters={filters}
              currency={summary.data.currency}
              disabled={statement.data?.totalCount === 0}
            />
          ) : null}
        </div>

        <StatementFiltersBar filters={filters} onChange={applyFilters} />

        <Card className="overflow-hidden">
          {statement.isError ? (
            <div className="p-4">
              <ErrorState
                title="We could not load your statement"
                description="Nothing is wrong with your balance. Please try again."
                onRetry={() => void statement.refetch()}
              />
            </div>
          ) : null}

          {statement.isPending ? <StatementTableSkeleton /> : null}

          {statement.data && statement.data.totalCount === 0 ? (
            <EmptyState
              title="Nothing to show"
              description={
                isFiltered
                  ? 'No movements match these filters. Try widening the date range, or clear the filters.'
                  : 'Once you add funds or make a booking, every movement will be listed here.'
              }
              className="border-0"
            />
          ) : null}

          {statement.data && statement.data.totalCount > 0 && summary.data ? (
            <StatementTable
              transactions={statement.data.transactions}
              currency={summary.data.currency}
              filtered={isFiltered}
            />
          ) : null}
        </Card>

        {statement.data && statement.data.totalCount > 0 ? (
          <StatementPagination
            page={page}
            pageSize={STATEMENT_PAGE_SIZE}
            totalCount={statement.data.totalCount}
            onPageChange={setPage}
            busy={statement.isFetching}
          />
        ) : null}
      </section>
    </main>
  );
}
