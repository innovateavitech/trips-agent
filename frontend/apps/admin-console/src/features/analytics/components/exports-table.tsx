import { Badge } from '@trips/ui';
import { formatDateTime } from '../../../lib/format';
import type { ReportExport } from '../types';

/**
 * Who took data out of the platform, and how much of it.
 *
 * FRD §2.15 UC-1C RS-6 requires that every export be logged; this is where somebody reads it. The
 * cross-tenant rows are the ones that matter — an export with no agency covered every agency — so
 * they are called out rather than left for the reader to infer from an empty column.
 */
export function ExportsTable({ exports }: { exports: ReportExport[] }) {
  if (exports.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        Nothing has been exported yet. Every export will appear here with who took it, what it
        covered and how many rows it held.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[48rem] border-collapse text-sm">
        <caption className="sr-only">Every recorded export</caption>
        <thead>
          <tr className="border-b border-border text-left text-xs uppercase tracking-wide text-muted-foreground">
            <th scope="col" className="px-3 py-2 font-medium">
              When
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              What
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              Reach
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              Actor
            </th>
            <th scope="col" className="px-3 py-2 text-right font-medium">
              Rows
            </th>
          </tr>
        </thead>

        <tbody>
          {exports.map((entry) => (
            <tr key={entry.id} className="border-b border-border last:border-0">
              <td className="whitespace-nowrap px-3 py-3 text-muted-foreground">
                {formatDateTime(entry.exportedAt)}
              </td>
              <td className="px-3 py-3">
                <span className="block text-foreground">{entry.scopeDescription}</span>
                <span className="text-xs text-muted-foreground">{entry.definitionCode}</span>
              </td>
              <td className="px-3 py-3">
                <Badge tone={entry.scope === 'Platform' ? 'warning' : 'neutral'}>
                  {entry.scope === 'Platform' ? 'Every agency' : 'One agency'}
                </Badge>
              </td>
              <td className="px-3 py-3">
                <span className="block text-foreground">{entry.actorType}</span>
                <span className="text-xs text-muted-foreground">
                  {entry.actorUserId ?? 'No signed-in user'}
                </span>
              </td>
              <td className="px-3 py-3 text-right tabular-nums">
                {entry.rowCount.toLocaleString('en-NG')}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
