import { Link } from 'react-router-dom';
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from '@trips/ui';
import { EmptyState } from '../../../components/states';
import { formatDateTime } from '../../../lib/format';
import { alertHref, alertTypeLabel, severityTone } from '../dashboard-rules';
import type { AdminAlert } from '../types';

/**
 * The operational alerts queue, most urgent first and oldest within that.
 *
 * These are written by the booking pipeline, the nightly ledger audit and the supplier poller —
 * this screen only reads them. Each one that concerns an agency links to it, because the next
 * thing anybody does with an alert is go and look at whose it is.
 */
export function AlertsList({ alerts }: { alerts: AdminAlert[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Alerts</CardTitle>
        <CardDescription>
          Raised by the booking pipeline, the nightly books audit and the supplier poller.
        </CardDescription>
      </CardHeader>

      <CardContent className="p-0">
        {alerts.length === 0 ? (
          <EmptyState title="Nothing is waiting">
            No alert is open. New ones appear here as soon as a job raises them.
          </EmptyState>
        ) : (
          <ul className="flex flex-col divide-y divide-border">
            {alerts.map((alert) => {
              const href = alertHref(alert);

              return (
                <li key={alert.id} className="flex flex-col gap-1 px-4 py-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge tone={severityTone(alert.severity)}>{alert.severity}</Badge>
                    <span className="text-sm font-medium text-foreground">
                      {alertTypeLabel(alert.type)}
                    </span>
                    {alert.status === 'Acknowledged' ? (
                      <Badge tone="info">Someone is on it</Badge>
                    ) : null}
                    <span className="ml-auto whitespace-nowrap text-xs tabular-nums text-muted-foreground">
                      {formatDateTime(alert.createdAt)}
                    </span>
                  </div>

                  <p className="text-sm text-muted-foreground">{alert.message}</p>

                  {href && alert.agencyName ? (
                    <Link
                      to={href}
                      className="w-fit text-sm text-primary underline-offset-4 hover:underline"
                    >
                      {alert.agencyName}
                    </Link>
                  ) : null}
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
