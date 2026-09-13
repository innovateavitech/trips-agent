import { ArrowLeft } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Card, ErrorState, LoadingState } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { formatDay } from '../../departures/departure-rules';
import { useCustomer } from '../crm-api';
import { describeTrip, relativeTime } from '../crm-rules';
import {
  AddTaskForm,
  LogMessageForm,
  QuoteStatusBadge,
  StageBadge,
  TaskList,
  Timeline,
} from '../components/crm-parts';

/** Build plan F7 — one customer, all of them: what they spent, booked, asked and were told. */
export function CustomerPage() {
  const { customerId = '' } = useParams();
  const customer = useCustomer(customerId);

  if (customer.isPending) return <LoadingState size="page" label="Opening the customer" />;
  if (customer.isError) {
    return (
      <ErrorState
        {...describeError(customer.error)}
        onRetry={() => void customer.refetch()}
        retrying={customer.isFetching}
      />
    );
  }

  const person = customer.data;
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, person.currency);

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to="/crm/customers"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          Customers
        </Link>
      </div>

      <PageHeader
        title={person.name}
        description={
          [person.email, person.phone].filter(Boolean).join(' · ') || 'No contact details'
        }
      />

      <dl className="grid gap-4 sm:grid-cols-3">
        <Stat label="Spent with you">
          {person.lifetimeValueMinor > 0 ? money(person.lifetimeValueMinor) : '—'}
        </Stat>
        <Stat label="Bookings">{person.totalBookings}</Stat>
        <Stat label="Open leads">{person.openLeadCount}</Stat>
      </dl>

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <Panel title="Bookings">
            {person.bookings.length === 0 ? (
              <p className="text-sm text-muted-foreground">No bookings yet.</p>
            ) : (
              <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
                {person.bookings.map((booking) => (
                  <li
                    key={booking.reference}
                    className="flex flex-wrap justify-between gap-2 px-4 py-3 text-sm"
                  >
                    <span>
                      <Link
                        to={`/bookings/${booking.reference}`}
                        className="font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                      >
                        {booking.reference}
                      </Link>{' '}
                      <span className="text-muted-foreground">
                        · {booking.title}
                        {booking.travelDate ? ` · ${formatDay(booking.travelDate)}` : ''} ·{' '}
                        {booking.status}
                      </span>
                    </span>
                    <span className="tabular-nums text-foreground">
                      {money(booking.amountMinor)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="Leads and quotes">
            {person.leads.length === 0 ? (
              <p className="text-sm text-muted-foreground">No leads.</p>
            ) : null}
            <ul className="flex flex-col gap-2">
              {person.leads.map((lead) => (
                <li
                  key={lead.id}
                  className="flex flex-wrap items-center justify-between gap-2 text-sm"
                >
                  <Link
                    to={`/crm/leads/${lead.id}`}
                    className="text-foreground underline-offset-4 hover:text-primary hover:underline"
                  >
                    {describeTrip(lead)}
                  </Link>
                  <StageBadge stage={lead.stage} />
                </li>
              ))}
              {person.quotes.map((quote) => (
                <li
                  key={quote.id}
                  className="flex flex-wrap items-center justify-between gap-2 text-sm"
                >
                  <Link
                    to={`/crm/quotes/${quote.id}`}
                    className="text-foreground underline-offset-4 hover:text-primary hover:underline"
                  >
                    {quote.quoteNumber} · {quote.title} · {money(quote.totalMinor)}
                  </Link>
                  <QuoteStatusBadge status={quote.status} />
                </li>
              ))}
            </ul>
          </Panel>

          <Panel title="Messages">
            <LogMessageForm related={{ type: 'Customer', id: person.id }} />
            <Timeline messages={person.communications} />
          </Panel>
        </div>

        <aside className="flex flex-col gap-4">
          <Panel title="Tasks">
            <TaskList tasks={person.tasks} />
            <AddTaskForm related={{ type: 'Customer', id: person.id }} />
          </Panel>
          <p className="text-xs text-muted-foreground">
            Customer since {relativeTime(person.createdAt)}.
          </p>
        </aside>
      </div>
    </div>
  );
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card className="flex flex-col gap-4 p-5">
      <h2 className="text-base font-semibold text-foreground">{title}</h2>
      {children}
    </Card>
  );
}

function Stat({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Card className="p-4">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-1 text-2xl font-semibold tabular-nums text-foreground">{children}</dd>
    </Card>
  );
}
