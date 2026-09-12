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
import { formatDateTime, formatMoney } from '../../../lib/format';
import { dunningSummary, subscriptionStatusDisplay } from '../billing-rules';
import type { Subscriber } from '../types';

/**
 * Every agency on a plan, and what each owes.
 *
 * Ordered by when their period ends, so the ones about to be charged are at the top — which is the
 * order somebody watching for a billing problem wants to read them in.
 */
export function SubscriberTable({ subscribers }: { subscribers: Subscriber[] }) {
  return (
    <Table>
      <TableCaption className="pb-3">
        Agencies with a live subscription, soonest renewal first. An agency past due is still
        trading: the retry schedule has a week to run before anything is taken away.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col">Agency</TableHead>
          <TableHead scope="col">Plan</TableHead>
          <TableHead scope="col">Status</TableHead>
          <TableHead scope="col">Per month</TableHead>
          <TableHead scope="col">Renews</TableHead>
          <TableHead scope="col">Owing</TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {subscribers.map((subscriber) => {
          const status = subscriptionStatusDisplay(subscriber.status);
          const dunning = dunningSummary(subscriber);

          return (
            <TableRow key={subscriber.subscriptionId}>
              <TableCell>
                <span className="font-medium text-foreground">{subscriber.agencyName}</span>
                <span className="block text-xs text-muted-foreground">
                  Agency status: {subscriber.agencyStatus}
                </span>
              </TableCell>

              <TableCell>{subscriber.tierName}</TableCell>

              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
                {dunning ? (
                  <span className="block pt-1 text-xs text-muted-foreground">{dunning}</span>
                ) : null}
                {subscriber.trialEndsAt ? (
                  <span className="block pt-1 text-xs text-muted-foreground">
                    Trial ends {formatDateTime(subscriber.trialEndsAt)}
                  </span>
                ) : null}
              </TableCell>

              <TableCell>
                {subscriber.amountMinor === null
                  ? 'Free'
                  : formatMoney(subscriber.amountMinor, subscriber.currency)}
              </TableCell>

              <TableCell>{formatDateTime(subscriber.currentPeriodEnd)}</TableCell>

              <TableCell>
                {subscriber.outstandingMinor === 0 ? (
                  <span className="text-muted-foreground">Nothing</span>
                ) : (
                  <span className="font-medium text-destructive">
                    {formatMoney(subscriber.outstandingMinor, subscriber.currency)}
                  </span>
                )}
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

export function SubscriberTableSkeleton() {
  return (
    <div className="flex flex-col gap-3 p-5" aria-busy="true">
      {[0, 1, 2, 3].map((row) => (
        <Skeleton key={row} className="h-10 w-full" />
      ))}
    </div>
  );
}
