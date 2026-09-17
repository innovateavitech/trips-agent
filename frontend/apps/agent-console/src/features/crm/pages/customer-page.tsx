import { useState, type FormEvent, type ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  AddIcon,
  AirplaneIcon,
  Avatar,
  BackIcon,
  Badge,
  BookTravelIcon,
  Button,
  BusIcon,
  CalendarIcon,
  CustomerTypeIcon,
  EditIcon,
  EmailIcon,
  EmptyState,
  ErrorState,
  IconChip,
  LoadingState,
  NameIcon,
  PhoneIcon,
  ProgressBar,
  SegmentedControl,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Textarea,
  TravelIcon,
  buttonVariants,
  cn,
  type BadgeProps,
} from '@trips/ui';
import { formatMoneyParts, formatMoneyShort } from '@trips/utils';
import notesEmpty from '../../../assets/notes-empty.svg';
import notesSparkleLeft from '../../../assets/notes-sparkle-left.svg';
import notesSparkleRight from '../../../assets/notes-sparkle-right.svg';
import { describeError } from '../../../api/errors';
import { LIST_HEADER_CELL } from '../../../shell/list-table';
import { formatShortDate, formatShortDateTime } from '../../dashboard/booking-display';
import { INVOICE_STATUS_DISPLAY } from '../../invoices/invoice-display';
import { useCustomer, useLogCommunication } from '../crm-api';
import {
  PRODUCT_FILTERS,
  bookingsForProduct,
  customerInsights,
  customerTripStage,
  relativeTime,
  type CustomerTripStage,
  type ProductFilter,
} from '../crm-rules';
import { AddTaskForm, LogMessageForm, TaskList, Timeline } from '../components/crm-parts';
import type { Customer, CustomerBooking } from '../types';

type TabValue = 'bookings' | 'invoices' | 'travellers' | 'activity';

/**
 * The first three are the Figma "Customer details" frames (node 11:2). The
 * last keeps what this page did before the redesign — tasks and the message
 * log — which the frames do not show but the CRM still needs. Leads and
 * quotes are left out of this version on purpose.
 */
const TABS: ReadonlyArray<{ value: TabValue; label: string }> = [
  { value: 'bookings', label: 'Bookings' },
  { value: 'invoices', label: 'Invoices' },
  { value: 'travellers', label: 'Travellers' },
  { value: 'activity', label: 'Activity' },
];

const STAGE_DISPLAY: Record<
  CustomerTripStage,
  { label: string; tone: NonNullable<BadgeProps['tone']> }
> = {
  active: { label: 'Active', tone: 'info' },
  upcoming: { label: 'Upcoming', tone: 'warning' },
  completed: { label: 'Completed', tone: 'success' },
};

/** Build plan F7 — one customer, all of them: what they spent, booked, owe and were told. */
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

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to="/crm/customers"
          className="inline-flex items-center gap-1.5 text-base text-muted-foreground hover:text-foreground"
        >
          <BackIcon size={20} />
          Customers
        </Link>
      </div>

      <header className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex min-w-0 items-center gap-3">
          <Avatar name={person.name} size={60} tone="muted" />
          <div className="flex min-w-0 flex-col gap-0.5">
            <h1 className="truncate text-2xl font-semibold tracking-tight text-foreground">
              {person.name}
            </h1>
            <p className="text-base text-muted-foreground">{kindLabel(person)}</p>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {/* No endpoint edits a customer yet (F7): shown, and honest that it does nothing. */}
          <Button
            variant="secondary"
            radius="lg"
            disabled
            title="Editing a customer arrives with the CRM API"
          >
            <EditIcon size={16} />
            Edit
          </Button>
          <Link to="/search/flights" className={buttonVariants({ radius: 'lg' })}>
            <BookTravelIcon size={16} />
            Book travel
          </Link>
        </div>
      </header>

      <div className="grid items-start gap-8 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="flex min-w-0 flex-col gap-9">
          <SpendSummary customer={person} />
          <CustomerTabs customer={person} />
        </div>

        <aside className="flex flex-col gap-2">
          <DetailsCard customer={person} />
          <NotesCard customer={person} />
        </aside>
      </div>
    </div>
  );
}

function kindLabel(customer: Customer): string {
  return customer.kind === 'business' ? 'Business' : 'Individual';
}

/* ---------------------------------------------------------------- figures -- */

function SpendSummary({ customer }: { customer: Customer }) {
  const insights = customerInsights(customer.bookings);
  const total = customer.lifetimeValueMinor;
  const share = (part: number) => (total > 0 ? (part / total) * 100 : 0);

  return (
    <section aria-label="Spend" className="flex flex-col gap-6">
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-2">
          <p className="font-numeric text-sm text-muted-foreground">Total travel spend</p>
          <Money amountMinor={total} currency={customer.currency} size="lg" />
        </div>
        <ProgressBar
          segments={[
            {
              value: share(insights.flightMinor),
              colorClassName: 'bg-chart-1',
              label: `Flights: ${formatMoneyShort(insights.flightMinor, customer.currency)}`,
            },
            {
              value: share(insights.busMinor),
              colorClassName: 'bg-chart-2',
              label: `Buses: ${formatMoneyShort(insights.busMinor, customer.currency)}`,
            },
          ]}
        />
        <p className="sr-only">
          Flights {formatMoneyShort(insights.flightMinor, customer.currency)}, buses{' '}
          {formatMoneyShort(insights.busMinor, customer.currency)}, of{' '}
          {formatMoneyShort(total, customer.currency)} in all.
        </p>
      </div>

      <dl className="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3">
        <Figure label="Average trip cost">
          {insights.averageMinor !== null ? (
            <Money amountMinor={insights.averageMinor} currency={customer.currency} />
          ) : null}
        </Figure>
        <Figure label="Biggest trip">
          {insights.biggestMinor !== null ? (
            <Money amountMinor={insights.biggestMinor} currency={customer.currency} />
          ) : null}
        </Figure>
        <Figure label="Top destination">{insights.topRoute}</Figure>
        <Figure label="Top airline">{insights.topAirline}</Figure>
        <Figure label="Trips booked">{insights.tripCount}</Figure>
        <Figure label="Last booked">
          {insights.lastBookedAt ? formatShortDate(insights.lastBookedAt) : null}
        </Figure>
      </dl>
    </section>
  );
}

/** "₦10,450,356" with its kobo smaller and muted, as the wallet balance shows it. */
function Money({
  amountMinor,
  currency,
  size = 'md',
}: {
  amountMinor: number;
  currency: string;
  size?: 'md' | 'lg';
}) {
  const { whole, fraction } = formatMoneyParts(amountMinor, currency);
  return (
    <span
      className={cn(
        'font-numeric font-semibold leading-none tracking-tight text-foreground',
        size === 'lg' ? 'text-3xl' : 'text-xl',
      )}
    >
      {whole}
      <span className="text-xl text-muted-foreground">.{fraction}</span>
    </span>
  );
}

function Figure({ label, children }: { label: string; children: ReactNode }) {
  const empty = children === null || children === undefined || children === '';
  return (
    <div className="flex min-w-0 flex-col gap-2">
      <dt className="font-numeric text-sm text-muted-foreground">{label}</dt>
      <dd className="truncate text-xl font-semibold tracking-tight text-foreground">
        {empty ? <span className="text-muted-foreground">—</span> : children}
      </dd>
    </div>
  );
}

/* ------------------------------------------------------------------- tabs -- */

function CustomerTabs({ customer }: { customer: Customer }) {
  const [tab, setTab] = useState<TabValue>('bookings');

  return (
    <section aria-label="Customer records" className="flex flex-col gap-7">
      <div className="overflow-x-auto border-b border-border-subtle">
        <SegmentedControl
          label="Show"
          appearance="underline"
          options={TABS}
          value={tab}
          onChange={setTab}
          className="flex-nowrap"
        />
      </div>

      {tab === 'bookings' ? <BookingsTab customer={customer} /> : null}
      {tab === 'invoices' ? <InvoicesTab customer={customer} /> : null}
      {tab === 'travellers' ? <TravellersTab customer={customer} /> : null}
      {tab === 'activity' ? <ActivityTab customer={customer} /> : null}
    </section>
  );
}

function BookingsTab({ customer }: { customer: Customer }) {
  const [product, setProduct] = useState<ProductFilter>('all');
  const now = new Date();
  const visible = bookingsForProduct(customer.bookings, product);

  return (
    <div className="flex flex-col gap-4">
      <SegmentedControl
        label="Filter by travel type"
        appearance="pill"
        options={PRODUCT_FILTERS}
        value={product}
        onChange={setProduct}
      />

      {visible.length === 0 ? (
        <EmptyState
          title={customer.bookings.length === 0 ? 'No bookings yet' : 'No bookings of this kind'}
          action={
            customer.bookings.length === 0 ? (
              <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
                Book travel
              </Link>
            ) : undefined
          }
        >
          {customer.bookings.length === 0
            ? `Trips booked for ${customer.name} appear here, with where each one stands.`
            : `${customer.name} has ${customer.bookings.length} bookings, none of them ${product === 'bus' ? 'by bus' : 'flights'}.`}
        </EmptyState>
      ) : (
        <RecordTable headings={['Trip', 'Booking date', 'Status', 'Amount']} alignLastRight>
          {visible.map((booking) => (
            <BookingRow
              key={booking.reference}
              booking={booking}
              currency={customer.currency}
              now={now}
            />
          ))}
        </RecordTable>
      )}
    </div>
  );
}

function BookingRow({
  booking,
  currency,
  now,
}: {
  booking: CustomerBooking;
  currency: string;
  now: Date;
}) {
  const stage = customerTripStage(booking, now);
  const status = stage ? STAGE_DISPLAY[stage] : { label: booking.status, tone: 'neutral' as const };
  const ProductIcon =
    booking.product === 'flight' ? AirplaneIcon : booking.product === 'bus' ? BusIcon : TravelIcon;

  return (
    <TableRow className="h-16 border-border-subtle">
      <TableCell>
        <div className="flex items-center gap-2">
          <IconChip tone={booking.product ? 'info' : 'neutral'}>
            <ProductIcon size={20} />
          </IconChip>
          <div className="flex min-w-0 flex-col gap-0.5">
            <Link
              to={`/bookings/${booking.reference}`}
              className="truncate font-semibold text-foreground underline-offset-4 hover:text-primary hover:underline"
            >
              {booking.route ? `${booking.route.from} to ${booking.route.to}` : booking.title}
            </Link>
            <span className="truncate text-xs text-muted-foreground">
              {booking.carrier ?? booking.reference}
            </span>
          </div>
        </div>
      </TableCell>
      <TableCell className="whitespace-nowrap text-muted-foreground">
        {booking.bookedAt ? formatShortDate(booking.bookedAt) : '—'}
      </TableCell>
      <TableCell>
        <Badge tone={status.tone} dot>
          {status.label}
        </Badge>
      </TableCell>
      <TableCell className="whitespace-nowrap text-right tabular-nums text-foreground">
        {formatMoneyShort(booking.amountMinor, currency)}
      </TableCell>
    </TableRow>
  );
}

function InvoicesTab({ customer }: { customer: Customer }) {
  const invoices = customer.invoices ?? [];

  if (invoices.length === 0) {
    return (
      <EmptyState title="No invoices yet">
        An invoice appears here the first time you bill {customer.name} for a booking.
      </EmptyState>
    );
  }

  return (
    <RecordTable headings={['Amount', 'Invoice no.', 'Status', 'Date created']}>
      {invoices.map((invoice) => {
        const status = INVOICE_STATUS_DISPLAY[invoice.status];
        return (
          <TableRow key={invoice.id} className="h-14 border-border-subtle">
            <TableCell className="whitespace-nowrap font-semibold tabular-nums text-foreground">
              {formatMoneyShort(invoice.amountMinor, invoice.currency)}
            </TableCell>
            <TableCell className="whitespace-nowrap text-muted-foreground">
              {invoice.bookingReference ? (
                <Link
                  to={`/bookings/${invoice.bookingReference}`}
                  className="underline-offset-4 hover:text-primary hover:underline"
                >
                  {invoice.invoiceNumber}
                </Link>
              ) : (
                invoice.invoiceNumber
              )}
            </TableCell>
            <TableCell>
              <Badge tone={status.tone} dot>
                {status.label}
              </Badge>
            </TableCell>
            <TableCell className="whitespace-nowrap text-foreground">
              {formatShortDate(invoice.issuedAt)}
            </TableCell>
          </TableRow>
        );
      })}
    </RecordTable>
  );
}

function TravellersTab({ customer }: { customer: Customer }) {
  const travellers = customer.travellers ?? [];

  if (travellers.length === 0) {
    return (
      <EmptyState title="No travellers yet">
        Everyone who travels on {customer.name}&rsquo;s bookings is listed here, with the details a
        ticket needs.
      </EmptyState>
    );
  }

  return (
    <RecordTable headings={['Traveller', 'Title', 'Email', 'Date of birth', 'Gender']}>
      {travellers.map((traveller) => (
        <TableRow key={traveller.id} className="h-16 border-border-subtle">
          <TableCell>
            <div className="flex items-center gap-2">
              <Avatar name={traveller.name} size={40} tone="muted" />
              <span className="truncate font-semibold text-foreground">{traveller.name}</span>
            </div>
          </TableCell>
          <TableCell className="text-muted-foreground">{traveller.title ?? '—'}</TableCell>
          <TableCell
            className="max-w-56 truncate text-foreground"
            title={traveller.email ?? undefined}
          >
            {traveller.email ?? <span className="text-muted-foreground">—</span>}
          </TableCell>
          <TableCell className="whitespace-nowrap text-muted-foreground">
            {traveller.dateOfBirth
              ? formatShortDate(`${traveller.dateOfBirth}T12:00:00+01:00`)
              : '—'}
          </TableCell>
          <TableCell className="text-muted-foreground">{traveller.gender ?? '—'}</TableCell>
        </TableRow>
      ))}
    </RecordTable>
  );
}

function ActivityTab({ customer }: { customer: Customer }) {
  const related = { type: 'Customer' as const, id: customer.id };
  // Notes have their own card beside the tabs; the log here is calls and messages.
  const messages = customer.communications.filter((message) => message.channel !== 'Note');

  return (
    <div className="flex flex-col gap-8">
      <section className="flex flex-col gap-4">
        <h2 className="text-base font-semibold text-foreground">Tasks</h2>
        <TaskList tasks={customer.tasks} />
        <AddTaskForm related={related} />
      </section>
      <section className="flex flex-col gap-4">
        <h2 className="text-base font-semibold text-foreground">Messages</h2>
        <LogMessageForm related={related} />
        <Timeline messages={messages} />
      </section>
    </div>
  );
}

function RecordTable({
  headings,
  alignLastRight = false,
  children,
}: {
  headings: string[];
  alignLastRight?: boolean;
  children: ReactNode;
}) {
  return (
    <div className="overflow-x-auto rounded-xl">
      <Table>
        <TableHeader>
          <TableRow className="border-none hover:bg-transparent">
            {headings.map((heading, index) => (
              <TableHead
                key={heading}
                className={cn(
                  LIST_HEADER_CELL,
                  alignLastRight && index === headings.length - 1 && 'text-right',
                )}
              >
                {heading}
              </TableHead>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>{children}</TableBody>
      </Table>
    </div>
  );
}

/* ------------------------------------------------------------ right column -- */

function DetailsCard({ customer }: { customer: Customer }) {
  const rows: Array<{ label: string; icon: ReactNode; value: string | null }> = [
    { label: 'Name', icon: <NameIcon size={16} />, value: customer.name },
    { label: 'Customer type', icon: <CustomerTypeIcon size={16} />, value: kindLabel(customer) },
    { label: 'Email', icon: <EmailIcon size={16} />, value: customer.email },
    { label: 'Phone number', icon: <PhoneIcon size={16} />, value: customer.phone },
    {
      label: 'Date added',
      icon: <CalendarIcon size={16} />,
      value: formatShortDateTime(customer.createdAt),
    },
  ];

  return (
    <section className="flex flex-col gap-6 rounded-[2rem] bg-card p-5">
      <h2 className="text-base font-medium text-foreground">Customer details</h2>
      <dl className="flex flex-col gap-6">
        {rows.map((row) => (
          <div key={row.label} className="flex items-center justify-between gap-3">
            <dt className="flex shrink-0 items-center gap-2 text-sm text-muted-foreground">
              {row.icon}
              {row.label}
            </dt>
            <dd
              className="min-w-0 truncate text-right text-sm text-foreground"
              title={row.value ?? undefined}
            >
              {row.value ?? <span className="text-muted-foreground">—</span>}
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function NotesCard({ customer }: { customer: Customer }) {
  const notes = customer.communications.filter((message) => message.channel === 'Note');
  const [adding, setAdding] = useState(false);

  return (
    <section className="flex flex-col gap-5 rounded-[2rem] bg-card p-5">
      <h2 className="text-base font-medium text-foreground">Notes</h2>

      {notes.length > 0 ? (
        <ol aria-label="Notes" className="flex flex-col gap-4">
          {notes.map((note) => (
            <li key={note.id} className="flex flex-col gap-1">
              <p className="whitespace-pre-line text-sm text-foreground">{note.summary}</p>
              <p className="text-xs text-muted-foreground">
                {note.byName} · {relativeTime(note.at)}
              </p>
            </li>
          ))}
        </ol>
      ) : null}

      {notes.length === 0 && !adding ? (
        <div className="flex flex-col items-center gap-7 pb-6 pt-2 text-center">
          <div className="flex flex-col items-center gap-1">
            <NotesIllustration />
            <p className="text-sm text-muted-foreground">You don&rsquo;t have any notes yet.</p>
          </div>
          <AddNoteButton onClick={() => setAdding(true)} />
        </div>
      ) : null}

      {adding ? (
        <NoteForm customerId={customer.id} onDone={() => setAdding(false)} />
      ) : notes.length > 0 ? (
        <div>
          <AddNoteButton onClick={() => setAdding(true)} />
        </div>
      ) : null}
    </section>
  );
}

function AddNoteButton({ onClick }: { onClick: () => void }) {
  return (
    <Button variant="secondary" size="sm" radius="lg" onClick={onClick}>
      <AddIcon size={16} />
      Add new note
    </Button>
  );
}

function NoteForm({ customerId, onDone }: { customerId: string; onDone: () => void }) {
  const log = useLogCommunication();
  const [text, setText] = useState('');

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!text.trim()) return;
    log.mutate(
      {
        channel: 'Note',
        direction: 'Outbound',
        summary: text.trim(),
        related: { type: 'Customer', id: customerId },
      },
      { onSuccess: onDone },
    );
  }

  return (
    <form onSubmit={submit} aria-label="Add a note" className="flex flex-col gap-3">
      <Textarea
        label="New note"
        rows={3}
        autoFocus
        placeholder="Prefers aisle seats; invoices go to accounts@…"
        value={text}
        onChange={(event) => setText(event.target.value)}
        error={log.isError ? describeError(log.error).title : undefined}
      />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" size="sm" onClick={onDone}>
          Cancel
        </Button>
        <Button
          type="submit"
          size="sm"
          loading={log.isPending}
          disabled={!text.trim() || log.isPending}
        >
          Save note
        </Button>
      </div>
    </form>
  );
}

/**
 * The Figma empty-notes drawing, from its exported SVGs, at the frame's own
 * geometry: a 109×94 box, the notepad centred, one sparkle bottom-left and a
 * second, turned 34.69°, at the right.
 */
function NotesIllustration() {
  return (
    <div aria-hidden="true" className="relative" style={{ width: 109, height: 94.11 }}>
      <img
        src={notesEmpty}
        alt=""
        className="absolute top-0 -translate-x-1/2"
        style={{ left: 'calc(50% - 0.64px)', width: 94.11, height: 94.11 }}
      />
      <img
        src={notesSparkleLeft}
        alt=""
        className="absolute left-0"
        style={{ top: '67.47%', width: 18.71, height: 18.71 }}
      />
      <span
        className="absolute flex items-center justify-center"
        style={{ top: '28.66%', left: '81.42%', width: 20.25, height: 20.24 }}
      >
        <img
          src={notesSparkleRight}
          alt=""
          style={{ width: 14.55, height: 14.55, transform: 'rotate(34.69deg)' }}
        />
      </span>
    </div>
  );
}
