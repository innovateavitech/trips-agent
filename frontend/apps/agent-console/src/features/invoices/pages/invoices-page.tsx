import { Link } from 'react-router-dom';
import {
  Badge,
  EmptyState,
  ErrorState,
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
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useInvoices } from '../invoices-api';
import { INVOICE_STATUS_DISPLAY } from '../invoice-display';

const dateFormat = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
});

/**
 * Every invoice raised to a traveller for a booking. Not to be confused with
 * Billing, which is what Trips charges this agency — see `../types.ts`.
 */
export function InvoicesPage() {
  const invoices = useInvoices();

  return (
    <>
      <PageHeader
        title="Invoices"
        description="What your travellers owe you, and what they have already paid."
      />

      <div className="rounded-[2rem] bg-card">
        {invoices.isPending ? (
          <div className="flex flex-col gap-3 p-5" aria-busy="true" aria-label="Loading invoices">
            {[0, 1, 2, 3].map((row) => (
              <Skeleton key={row} className="h-10 w-full" />
            ))}
          </div>
        ) : null}

        {invoices.isError ? (
          <div className="p-5">
            <ErrorState
              {...describeError(invoices.error)}
              onRetry={() => void invoices.refetch()}
            />
          </div>
        ) : null}

        {invoices.data && invoices.data.length === 0 ? (
          <EmptyState title="No invoices yet">
            An invoice appears here the first time you bill a traveller for a booking.
          </EmptyState>
        ) : null}

        {invoices.data && invoices.data.length > 0 ? (
          <Table>
            <TableCaption className="pb-3">
              Your travellers&rsquo; invoices. Trips&rsquo; own charges to you are under Billing.
            </TableCaption>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead scope="col">Invoice</TableHead>
                <TableHead scope="col">Customer</TableHead>
                <TableHead scope="col">Issued</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col" className="text-right">
                  Amount
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {invoices.data.map((invoice) => {
                const status = INVOICE_STATUS_DISPLAY[invoice.status];
                return (
                  <TableRow key={invoice.id}>
                    <TableCell>
                      {invoice.bookingReference ? (
                        <Link
                          to={`/bookings/${invoice.bookingReference}`}
                          className="font-mono text-xs text-primary underline-offset-4 hover:underline"
                        >
                          {invoice.invoiceNumber}
                        </Link>
                      ) : (
                        <span className="font-mono text-xs text-foreground">
                          {invoice.invoiceNumber}
                        </span>
                      )}
                    </TableCell>
                    <TableCell className="font-medium">{invoice.customerName}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {dateFormat.format(new Date(invoice.issuedAt))}
                    </TableCell>
                    <TableCell>
                      <Badge tone={status.tone}>{status.label}</Badge>
                    </TableCell>
                    <TableCell className="text-right font-medium tabular-nums">
                      {formatMoney(invoice.amountMinor, invoice.currency)}
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        ) : null}
      </div>
    </>
  );
}
