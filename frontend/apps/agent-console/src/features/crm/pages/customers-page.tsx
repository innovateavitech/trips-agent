import { Users } from 'lucide-react';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  AddIcon,
  Avatar,
  Button,
  EmptyState,
  ErrorState,
  GlobalSearchInput,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  cn,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { DateRangeMenu, NO_RANGE, type DateRange } from '../../../shell/date-range-menu';
import { LIST_HEADER_CELL, ListSkeleton } from '../../../shell/list-table';
import { PageHeader } from '../../../shell/page-header';
import { formatShortDateTime } from '../../dashboard/booking-display';
import { AddCustomerSheet } from '../components/add-customer-sheet';
import { useCustomers } from '../crm-api';
import { filterCustomers } from '../crm-rules';
import type { CustomerSummary } from '../types';

/**
 * Build plan F7 — everyone the agency sells travel to, laid out as the Figma
 * "Customers" frame (node 133:2140): the same list anatomy as Travel, so the
 * two screens read as one product.
 *
 * "Add customer" opens a panel over the list (`AddCustomerSheet`); saving
 * goes straight to the new customer's page.
 */
export function CustomersPage() {
  const customers = useCustomers();
  const navigate = useNavigate();
  const [adding, setAdding] = useState(false);
  const [query, setQuery] = useState('');
  const [lastBooking, setLastBooking] = useState<DateRange>(NO_RANGE);
  const [dateAdded, setDateAdded] = useState<DateRange>(NO_RANGE);

  const all = customers.data ?? [];
  const visible = filterCustomers(all, { query, lastBooking, dateAdded });
  const filtersActive = Boolean(
    query.trim() || lastBooking.from || lastBooking.to || dateAdded.from || dateAdded.to,
  );

  const addCustomer = (
    <Button radius="lg" onClick={() => setAdding(true)}>
      <AddIcon size={16} />
      Add customer
    </Button>
  );

  return (
    <div className="flex flex-col gap-6">
      <AddCustomerSheet
        open={adding}
        onOpenChange={setAdding}
        onCreated={(customer) => navigate(`/crm/customers/${customer.id}`)}
      />
      <PageHeader title="Customers" actions={addCustomer} />

      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex flex-wrap gap-2">
            <DateRangeMenu label="Last booking" range={lastBooking} onChange={setLastBooking} />
            <DateRangeMenu label="Date added" range={dateAdded} onChange={setDateAdded} />
          </div>
          <GlobalSearchInput
            className="w-full max-w-[320px]"
            placeholder="Filter by name"
            aria-label="Filter by name, email or phone"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </div>

        {customers.isPending ? <ListSkeleton label="Loading your customers" /> : null}

        {customers.isError ? (
          <ErrorState
            {...describeError(customers.error)}
            onRetry={() => void customers.refetch()}
            retrying={customers.isFetching}
          />
        ) : null}

        {customers.data && all.length === 0 ? (
          <EmptyState
            size="page"
            headingLevel={2}
            icon={<Users aria-hidden="true" className="h-5 w-5" />}
            title="No customers yet"
            action={addCustomer}
          >
            Add the people and businesses you sell travel to. Anyone who books is added here too.
          </EmptyState>
        ) : null}

        {customers.data && all.length > 0 && visible.length === 0 ? (
          <EmptyState
            title="No customers match"
            action={
              filtersActive ? (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => {
                    setQuery('');
                    setLastBooking(NO_RANGE);
                    setDateAdded(NO_RANGE);
                  }}
                >
                  Clear filters
                </Button>
              ) : undefined
            }
          >
            {all.length} customers exist; the filters are hiding all of them.
          </EmptyState>
        ) : null}

        {visible.length > 0 ? <CustomersTable customers={visible} /> : null}
      </div>
    </div>
  );
}

function CustomersTable({ customers }: { customers: CustomerSummary[] }) {
  return (
    <div className="overflow-x-auto rounded-xl">
      <Table>
        <TableHeader>
          <TableRow className="border-none hover:bg-transparent">
            <TableHead className={LIST_HEADER_CELL}>Customer</TableHead>
            <TableHead className={LIST_HEADER_CELL}>Bookings</TableHead>
            <TableHead className={LIST_HEADER_CELL}>Email</TableHead>
            <TableHead className={LIST_HEADER_CELL}>Last booking</TableHead>
            <TableHead className={LIST_HEADER_CELL}>Date added</TableHead>
            <TableHead className={cn(LIST_HEADER_CELL, 'text-right')}>Total spend</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {customers.map((customer) => (
            <TableRow key={customer.id} className="h-16 border-border-subtle">
              <TableCell>
                <div className="flex items-center gap-2">
                  <Avatar name={customer.name} size={40} tone="muted" />
                  <div className="flex min-w-0 flex-col gap-0.5">
                    <Link
                      to={`/crm/customers/${customer.id}`}
                      className="truncate font-semibold text-foreground underline-offset-4 hover:text-primary hover:underline"
                    >
                      {customer.name}
                    </Link>
                    <span className="text-xs text-muted-foreground">
                      {customer.kind === 'business' ? 'Business' : 'Individual'}
                    </span>
                  </div>
                </div>
              </TableCell>
              <TableCell>
                <span className="inline-flex size-7 items-center justify-center rounded-lg bg-muted text-sm font-semibold tabular-nums text-foreground">
                  {customer.totalBookings}
                </span>
              </TableCell>
              <TableCell className="whitespace-nowrap text-foreground">
                {customer.email ?? <span className="text-muted-foreground">—</span>}
              </TableCell>
              <TableCell className="whitespace-nowrap text-muted-foreground">
                {customer.lastBookingAt ? formatShortDateTime(customer.lastBookingAt) : '—'}
              </TableCell>
              <TableCell className="whitespace-nowrap text-muted-foreground">
                {customer.createdAt ? formatShortDateTime(customer.createdAt) : '—'}
              </TableCell>
              <TableCell className="whitespace-nowrap text-right font-semibold tabular-nums text-foreground">
                {customer.lifetimeValueMinor > 0
                  ? formatMoneyShort(customer.lifetimeValueMinor, 'NGN')
                  : '—'}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
