import { ArrowLeft } from 'lucide-react';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  ErrorState,
  LoadingState,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { canEditCatalog, canPublishCatalog } from '../../catalog/catalog-rules';
import { formatPercent } from '../../pricing/pricing-rules';
import {
  describeGuarantee,
  describeSeats,
  formatDay,
  lowestPriceMinor,
  paymentSchedule,
  plusDays,
  todayInLagos,
} from '../departure-rules';
import { useDeparture, useDepartureAction, useManifest, useWaitlist } from '../departures-api';
import { DepartureStatusBadge, SeatsBar } from '../components/departure-parts';
import type { Departure, DepartureAction } from '../types';

const ACTION_COPY: Record<
  DepartureAction,
  { title: string; body: string; confirm: string; destructive?: boolean }
> = {
  close: {
    title: 'Stop new bookings?',
    body: 'Customers can no longer book this departure. Everyone already booked keeps their seat, and you can reopen it.',
    confirm: 'Close bookings',
  },
  reopen: {
    title: 'Reopen bookings?',
    body: 'Customers can book this departure again, while seats last.',
    confirm: 'Reopen',
  },
  cancel: {
    title: 'Cancel this departure?',
    body: 'It will not run. Nobody has paid for it, so there is nothing to refund.',
    confirm: 'Cancel departure',
    destructive: true,
  },
};

/** Build plan F6 — one departure: its seats, whether it runs, its terms, who is on it, who is waiting. */
export function DeparturePage() {
  const { departureId = '' } = useParams();
  const departure = useDeparture(departureId);

  if (departure.isPending) return <LoadingState size="page" label="Opening the departure" />;
  if (departure.isError) {
    return (
      <ErrorState
        {...describeError(departure.error)}
        onRetry={() => void departure.refetch()}
        retrying={departure.isFetching}
      />
    );
  }

  return <DepartureView departure={departure.data} />;
}

function DepartureView({ departure }: { departure: Departure }) {
  const user = useCurrentUser();
  const action = useDepartureAction(departure.id);
  const [confirming, setConfirming] = useState<DepartureAction | null>(null);
  const canEdit = canEditCatalog(user.roles) && departure.status !== 'Cancelled';
  const canAct = canPublishCatalog(user.roles) && departure.status !== 'Cancelled';
  const closed = departure.status === 'Closed';
  const guarantee = describeGuarantee(departure);
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, departure.currency);
  const lowest = lowestPriceMinor(departure.priceTiers);
  const schedule = lowest === null ? [] : paymentSchedule(departure, todayInLagos(), lowest);
  const copy = confirming ? ACTION_COPY[confirming] : null;

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to={`/departures?product=${departure.productId}`}
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          {departure.productTitle}
        </Link>
      </div>

      <PageHeader
        title={formatDay(departure.departureDate)}
        description={`${departure.productTitle} · bookings close ${formatDay(
          plusDays(departure.departureDate, -departure.cutoffDaysBefore),
        )}`}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <DepartureStatusBadge status={departure.status} />
            {canEdit ? (
              <Link
                to={`/departures/${departure.id}/edit`}
                className={buttonVariants({ variant: 'outline', size: 'sm' })}
              >
                Edit
              </Link>
            ) : null}
            {canAct ? (
              <Button
                variant="outline"
                size="sm"
                onClick={() => setConfirming(closed ? 'reopen' : 'close')}
              >
                {closed ? 'Reopen bookings' : 'Close bookings'}
              </Button>
            ) : null}
            {canAct ? (
              <Button variant="ghost" size="sm" onClick={() => setConfirming('cancel')}>
                Cancel departure
              </Button>
            ) : null}
          </div>
        }
      />

      {action.isError ? <ErrorState {...describeError(action.error)} /> : null}

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <Card className="flex flex-col gap-3 p-5">
            <h2 className="text-base font-semibold text-foreground">Seats</h2>
            <SeatsBar departure={departure} />
            <p className="text-sm text-foreground">
              {describeSeats(departure)} · {departure.seatsLeft} left
            </p>
            {guarantee ? (
              <p
                className={
                  departure.capacityConfirmed >= departure.minPax
                    ? 'text-sm text-success-subtle-foreground'
                    : 'text-sm text-muted-foreground'
                }
              >
                {guarantee}
              </p>
            ) : null}
          </Card>

          <Manifest departureId={departure.id} />
          <Waitlist departureId={departure.id} />
        </div>

        <aside className="flex flex-col gap-4">
          <Card className="flex flex-col gap-3 p-5">
            <h2 className="text-sm font-semibold text-foreground">Price per traveller</h2>
            <ul className="flex flex-col gap-1 text-sm">
              {departure.priceTiers.map((tier) => (
                <li key={tier.minPax} className="flex justify-between gap-2">
                  <span className="text-muted-foreground">
                    {tier.maxPax === null
                      ? `${tier.minPax} or more`
                      : tier.minPax === tier.maxPax
                        ? `${tier.minPax}`
                        : `${tier.minPax} to ${tier.maxPax}`}
                  </span>
                  <span className="tabular-nums text-foreground">
                    {money(tier.pricePerPaxMinor)}
                  </span>
                </li>
              ))}
            </ul>
            <p className="border-t border-border pt-3 text-sm text-muted-foreground">
              {departure.depositType === 'Percent' && departure.depositPercentBasisPoints !== null
                ? `Deposit ${formatPercent(departure.depositPercentBasisPoints)}% at booking.`
                : departure.depositType === 'Fixed' && departure.depositAmountMinor !== null
                  ? `Deposit ${money(departure.depositAmountMinor)} at booking.`
                  : 'No deposit.'}{' '}
              {departure.installments.length === 0
                ? 'The balance is due when bookings close.'
                : `The balance in ${departure.installments.length} payments.`}
            </p>
            {schedule.length > 0 ? (
              <ul
                aria-label="Payment schedule"
                className="flex flex-col gap-1 text-xs text-muted-foreground"
              >
                {schedule.map((line) => (
                  <li key={line.label} className="flex justify-between gap-2">
                    <span>
                      {line.label} · {line.dueNow ? 'at booking' : formatDay(line.dueDate)}
                    </span>
                    <span className="tabular-nums">{money(line.amountMinor)}</span>
                  </li>
                ))}
              </ul>
            ) : null}
          </Card>
        </aside>
      </div>

      <Dialog
        open={copy !== null}
        onOpenChange={(open) => (open ? undefined : setConfirming(null))}
      >
        {copy && confirming ? (
          <DialogContent>
            <DialogTitle>{copy.title}</DialogTitle>
            <DialogDescription>{copy.body}</DialogDescription>
            {confirming === 'cancel' && departure.capacityConfirmed > 0 ? (
              <Alert tone="warning" title={`${departure.capacityConfirmed} travellers have paid`}>
                What happens to their deposits is still an open question with the client, so this
                will be refused. Close bookings instead to stop new ones.
              </Alert>
            ) : null}
            <DialogFooter>
              <Button
                variant="outline"
                onClick={() => setConfirming(null)}
                disabled={action.isPending}
              >
                Not now
              </Button>
              <Button
                variant={copy.destructive ? 'destructive' : undefined}
                loading={action.isPending}
                onClick={() => action.mutate(confirming, { onSettled: () => setConfirming(null) })}
              >
                {copy.confirm}
              </Button>
            </DialogFooter>
          </DialogContent>
        ) : null}
      </Dialog>
    </div>
  );
}

function Manifest({ departureId }: { departureId: string }) {
  const manifest = useManifest(departureId);

  return (
    <Card className="flex flex-col gap-3 p-5">
      <h2 className="text-base font-semibold text-foreground">Manifest</h2>
      {manifest.isPending ? <p className="text-sm text-muted-foreground">Loading…</p> : null}
      {manifest.isError ? (
        <p className="text-sm text-destructive">{describeError(manifest.error).title}</p>
      ) : null}
      {manifest.data && manifest.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nobody has booked yet.</p>
      ) : null}
      {manifest.data && manifest.data.length > 0 ? (
        <div className="-mx-5 overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Traveller</TableHead>
                <TableHead>Booking</TableHead>
                <TableHead>Room</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {manifest.data.map((entry, index) => (
                <TableRow key={`${entry.orderReference}-${index}`}>
                  <TableCell className="text-foreground">
                    {entry.travellerName}
                    {entry.paxType === 'Adult' ? null : (
                      <span className="text-muted-foreground">
                        {' '}
                        · {entry.paxType.toLowerCase()}
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-muted-foreground">{entry.orderReference}</TableCell>
                  <TableCell className="text-muted-foreground">{entry.room ?? '—'}</TableCell>
                  <TableCell className="text-muted-foreground">
                    {entry.status === 'Confirmed' ? 'Paid' : 'Held in checkout'}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </Card>
  );
}

function Waitlist({ departureId }: { departureId: string }) {
  const waitlist = useWaitlist(departureId);
  if (!waitlist.data || waitlist.data.length === 0) return null;

  return (
    <Card className="flex flex-col gap-3 p-5">
      <h2 className="text-base font-semibold text-foreground">Waitlist</h2>
      <p className="text-sm text-muted-foreground">
        When a seat frees up, the first person waiting is offered it for a day, then the next.
      </p>
      <ol className="flex flex-col divide-y divide-border rounded-md border border-border">
        {waitlist.data.map((entry) => (
          <li key={entry.id} className="flex flex-wrap justify-between gap-2 px-4 py-3 text-sm">
            <span className="text-foreground">
              {entry.name} · {entry.paxCount} {entry.paxCount === 1 ? 'traveller' : 'travellers'}
            </span>
            <span className="text-muted-foreground">
              {entry.status === 'Offered' && entry.expiresAt
                ? `Offered a seat until ${new Date(entry.expiresAt).toLocaleString('en-NG', {
                    timeZone: 'Africa/Lagos',
                    day: 'numeric',
                    month: 'short',
                    hour: '2-digit',
                    minute: '2-digit',
                  })}`
                : entry.status}
            </span>
          </li>
        ))}
      </ol>
    </Card>
  );
}
