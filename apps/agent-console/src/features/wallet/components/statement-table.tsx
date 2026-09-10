import {
  Badge,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import type { Currency, WalletTransaction } from '../types';
import { TRANSACTION_TYPE_LABELS, TRANSACTION_TYPE_TONES } from '../transaction-display';

const DATE_FORMAT = new Intl.DateTimeFormat('en-NG', {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

export function StatementTable({
  transactions,
  currency,
  filtered,
}: {
  transactions: WalletTransaction[];
  currency: Currency;
  /** Changes the caption, because a running balance under a filter needs explaining. */
  filtered: boolean;
}) {
  return (
    <Table>
      <TableCaption>
        {filtered
          ? 'Balance is the wallet balance after each movement, including movements hidden by your filters — so it will not add up down the column.'
          : 'Balance is the wallet balance immediately after each movement.'}
      </TableCaption>

      <TableHeader>
        <TableRow>
          <TableHead scope="col">Date</TableHead>
          <TableHead scope="col">Type</TableHead>
          <TableHead scope="col">Description</TableHead>
          <TableHead scope="col">Reference</TableHead>
          <TableHead scope="col" className="text-right">
            Amount
          </TableHead>
          <TableHead scope="col" className="text-right">
            Balance
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {transactions.map((transaction) => {
          const isCredit = transaction.amountMinor > 0;

          return (
            <TableRow key={transaction.id}>
              <TableCell className="whitespace-nowrap text-muted-foreground">
                {DATE_FORMAT.format(new Date(transaction.occurredAt))}
              </TableCell>

              <TableCell>
                <Badge tone={TRANSACTION_TYPE_TONES[transaction.type]}>
                  {TRANSACTION_TYPE_LABELS[transaction.type]}
                </Badge>
              </TableCell>

              <TableCell className="min-w-64">{transaction.description}</TableCell>

              <TableCell className="whitespace-nowrap font-mono text-xs text-muted-foreground">
                {transaction.reference ?? '—'}
              </TableCell>

              <TableCell
                className={`whitespace-nowrap text-right font-medium tabular-nums ${
                  isCredit ? 'text-success-subtle-foreground' : 'text-foreground'
                }`}
              >
                {/* The sign is spelled out rather than left to the colour:
                    roughly one in twelve men cannot reliably tell our green
                    from our default text colour, and this is money. */}
                {isCredit ? '+' : '−'}
                {formatMoney(Math.abs(transaction.amountMinor), currency)}
              </TableCell>

              <TableCell className="whitespace-nowrap text-right tabular-nums text-muted-foreground">
                {formatMoney(transaction.balanceAfterMinor, currency)}
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

export function StatementTableSkeleton({ rows = 8 }: { rows?: number }) {
  return (
    <div aria-busy="true" aria-label="Loading your statement" className="flex flex-col gap-3 p-3">
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-9 w-full" />
      ))}
    </div>
  );
}
