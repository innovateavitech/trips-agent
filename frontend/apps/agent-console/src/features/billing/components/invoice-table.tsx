import { useState } from 'react';
import {
  Badge,
  Button,
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
import { invoiceAddsUp, invoiceStatusDisplay } from '../billing-rules';
import type { Invoice } from '../types';
import { formatDate } from '../format';

/**
 * Every subscription invoice, with its lines one click away.
 *
 * The lines are here rather than on a page of their own because the only question anyone brings to
 * this table is "what is this charge for?", and the answer is the lines. A paid invoice shows its
 * receipt number in the same row: the receipt is not a separate document, it is what a paid invoice
 * becomes.
 */
export function InvoiceTable({ invoices }: { invoices: Invoice[] }) {
  const [open, setOpen] = useState<string | null>(null);

  return (
    <Table>
      <TableCaption className="pb-3">
        What Trips has charged this agency. Your customers&rsquo; invoices are under Bookings —
        these are ours to you.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col">Invoice</TableHead>
          <TableHead scope="col">Period</TableHead>
          <TableHead scope="col">Status</TableHead>
          <TableHead scope="col">Total</TableHead>
          <TableHead scope="col">
            <span className="sr-only">Lines</span>
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {invoices.map((invoice) => {
          const status = invoiceStatusDisplay(invoice.status);
          const expanded = open === invoice.id;

          return [
            <TableRow key={invoice.id}>
              <TableCell>
                <span className="font-mono text-xs text-foreground">{invoice.invoiceNumber}</span>
                {invoice.receiptNumber ? (
                  <span className="block font-mono text-xs text-muted-foreground">
                    Receipt {invoice.receiptNumber}
                  </span>
                ) : null}
              </TableCell>

              <TableCell className="text-sm">
                {formatDate(invoice.periodStart)} – {formatDate(invoice.periodEnd)}
              </TableCell>

              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
                {invoice.statusReason ? (
                  <span className="block pt-1 text-xs text-muted-foreground">
                    {invoice.statusReason}
                  </span>
                ) : null}
              </TableCell>

              <TableCell className="font-medium">
                {formatMoney(invoice.totalMinor, invoice.currency)}
              </TableCell>

              <TableCell className="text-right">
                <Button
                  size="sm"
                  variant="ghost"
                  aria-expanded={expanded}
                  onClick={() => setOpen(expanded ? null : invoice.id)}
                >
                  {expanded ? 'Hide lines' : 'Show lines'}
                </Button>
              </TableCell>
            </TableRow>,

            expanded ? (
              <TableRow key={`${invoice.id}-lines`} className="hover:bg-transparent">
                <TableCell colSpan={5}>
                  <InvoiceLines invoice={invoice} />
                </TableCell>
              </TableRow>
            ) : null,
          ];
        })}
      </TableBody>
    </Table>
  );
}

/** The lines and the total, with the arithmetic visible. */
function InvoiceLines({ invoice }: { invoice: Invoice }) {
  const addsUp = invoiceAddsUp(invoice);

  return (
    <div className="flex flex-col gap-2 rounded-md bg-muted p-4">
      <ul className="flex flex-col gap-1">
        {invoice.lines.map((line, index) => (
          <li
            key={`${invoice.id}-${index}`}
            className="flex items-baseline justify-between gap-4 text-sm"
          >
            <span className="text-muted-foreground">
              {line.description}
              {line.quantity > 1 ? ` × ${line.quantity}` : ''}
            </span>
            <span className="font-medium text-foreground">
              {formatMoney(line.amountMinor, invoice.currency)}
            </span>
          </li>
        ))}
      </ul>

      <div className="flex items-baseline justify-between gap-4 border-t border-border pt-2 text-sm">
        <span className="font-medium text-foreground">Total</span>
        <span className="font-semibold text-foreground">
          {formatMoney(invoice.totalMinor, invoice.currency)}
        </span>
      </div>

      {/* Never expected: the server and the database both hold the total to the sum of the lines.
          Said out loud rather than hidden, because a total nobody can check is worse than one that
          admits it does not add up. */}
      {addsUp ? null : (
        <p className="text-xs text-destructive">
          These lines do not add up to the total shown. Please get in touch before paying it.
        </p>
      )}

      {invoice.paidAt ? (
        <p className="text-xs text-muted-foreground">Paid on {formatDate(invoice.paidAt)}.</p>
      ) : (
        <p className="text-xs text-muted-foreground">Due {formatDate(invoice.dueAt)}.</p>
      )}
    </div>
  );
}

export function InvoiceTableSkeleton() {
  return (
    <div className="flex flex-col gap-3 p-4" aria-busy="true">
      {[0, 1, 2].map((row) => (
        <Skeleton key={row} className="h-10 w-full" />
      ))}
    </div>
  );
}
