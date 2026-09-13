import { Badge } from '@trips/ui';
import { errorTone, formatBasisPoints } from '../analytics-rules';
import type { SupplierPerformanceRow } from '../types';

/**
 * How each supplier behaved: how often a search became a ticket, and how often a call failed.
 *
 * Timeouts have their own column rather than being folded into errors. A timed-out ticket issue is
 * an unknown outcome, not a failure — a real ticket may exist at the other end — and the response
 * to it is to poll the booking's status, never to send the call again (ADR-0003). Counting it as a
 * failure would both flatter and mislead.
 */
export function SupplierTable({ rows }: { rows: SupplierPerformanceRow[] }) {
  if (rows.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        No supplier calls were recorded in this window.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[56rem] border-collapse text-sm">
        <caption className="sr-only">Supplier performance for the selected window</caption>
        <thead>
          <tr className="border-b border-border text-left text-xs uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2 font-medium">
              Supplier
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Searches
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Tickets
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Search to book
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Calls
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Error rate
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Timeouts
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Latency
            </th>
          </tr>
        </thead>

        <tbody>
          {rows.map((row) => {
            const tone = errorTone(row.errorRateBasisPoints);

            return (
              <tr key={row.supplierId} className="border-b border-border last:border-0">
                <td className="px-3 py-3">
                  <span className="block font-medium text-foreground">{row.supplierName}</span>
                  <span className="text-xs text-muted-foreground">{row.supplierCode}</span>
                </td>
                <td className="px-3 py-3 text-right tabular-nums">
                  {row.searches.toLocaleString('en-NG')}
                </td>
                <td className="px-3 py-3 text-right tabular-nums">
                  {row.booked.toLocaleString('en-NG')}
                </td>
                <td className="px-3 py-3 text-right tabular-nums">
                  {formatBasisPoints(row.conversionBasisPoints)}
                </td>
                <td className="px-3 py-3 text-right tabular-nums">
                  {row.totalCalls.toLocaleString('en-NG')}
                </td>
                <td className="px-3 py-3 text-right">
                  <Badge
                    tone={
                      tone === 'urgent'
                        ? 'destructive'
                        : tone === 'attention'
                          ? 'warning'
                          : 'neutral'
                    }
                  >
                    {formatBasisPoints(row.errorRateBasisPoints)}
                  </Badge>
                </td>
                <td className="px-3 py-3 text-right tabular-nums">
                  {row.timeouts.toLocaleString('en-NG')}
                </td>
                <td className="px-3 py-3 text-right tabular-nums text-muted-foreground">
                  {row.averageLatencyMs.toLocaleString('en-NG')} ms
                  <span className="block text-xs">
                    peak {row.maxLatencyMs.toLocaleString('en-NG')} ms
                  </span>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
