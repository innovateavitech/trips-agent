import { Link } from 'react-router-dom';
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
  buttonVariants,
} from '@trips/ui';
import { countryName, formatDateTime, formatMoney } from '../../../lib/format';
import { agencyStatusDisplay } from '../agency-rules';
import type { AgencySummary } from '../types';

/**
 * The directory itself.
 *
 * Status sits second, right after the name, because it is the column that decides what to do
 * next — everything else on the row is context for that one fact.
 */
export function DirectoryTable({ agencies }: { agencies: AgencySummary[] }) {
  return (
    <Table>
      <TableCaption className="pb-3">
        Every travel agency on the platform. Wallet balances are what they hold with us now.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col">Agency</TableHead>
          <TableHead scope="col">Status</TableHead>
          <TableHead scope="col">Type</TableHead>
          <TableHead scope="col" className="text-right">
            Wallet
          </TableHead>
          <TableHead scope="col" className="text-right">
            Orders
          </TableHead>
          <TableHead scope="col">Signed up</TableHead>
          <TableHead scope="col">
            <span className="sr-only">Actions</span>
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {agencies.map((agency) => {
          const status = agencyStatusDisplay(agency.status);
          const href = `/agencies/${agency.id}`;

          return (
            <TableRow key={agency.id}>
              <TableCell className="min-w-56">
                <Link
                  to={href}
                  className="font-medium text-foreground underline-offset-4 hover:underline"
                >
                  {agency.name}
                </Link>
                <p className="text-xs text-muted-foreground">
                  {agency.slug} · {countryName(agency.countryCode)}
                </p>
              </TableCell>

              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
              </TableCell>

              <TableCell className="whitespace-nowrap text-muted-foreground">
                {agency.type === 'SubAgent' ? (
                  <>
                    Sub-agent
                    {agency.parentAgencyName ? (
                      <p className="text-xs">of {agency.parentAgencyName}</p>
                    ) : null}
                  </>
                ) : (
                  'Principal'
                )}
              </TableCell>

              <TableCell className="whitespace-nowrap text-right tabular-nums">
                {agency.walletBalanceMinor === null ? (
                  <span className="text-muted-foreground" title="No wallet until KYB is approved">
                    —
                  </span>
                ) : (
                  formatMoney(agency.walletBalanceMinor, agency.baseCurrency)
                )}
              </TableCell>

              <TableCell className="text-right tabular-nums">{agency.orderCount}</TableCell>

              <TableCell className="whitespace-nowrap tabular-nums text-muted-foreground">
                {formatDateTime(agency.createdAt)}
              </TableCell>

              <TableCell className="text-right">
                <Link to={href} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
                  Open<span className="sr-only"> {agency.name}</span>
                </Link>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/** Same footprint as the table, so the page does not jump when the directory arrives. */
export function DirectoryTableSkeleton({ rows = 8 }: { rows?: number }) {
  return (
    <div aria-busy="true" aria-label="Loading the directory" className="flex flex-col gap-3 p-4">
      <Skeleton className="h-4 w-1/3" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-10 w-full" />
      ))}
    </div>
  );
}
