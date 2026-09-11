import { Users } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Card,
  EmptyState,
  ErrorState,
  Input,
  LoadingState,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useCustomers } from '../crm-api';
import { relativeTime } from '../crm-rules';

/**
 * Build plan F7 — everyone who has asked, been quoted or booked. Nobody is
 * added here: a customer appears the first time they get in touch.
 */
export function CustomersPage() {
  const customers = useCustomers();
  const [query, setQuery] = useState('');
  const needle = query.trim().toLowerCase();
  const visible = (customers.data ?? [])
    .filter(
      (customer) =>
        !needle ||
        [customer.name, customer.email ?? '', customer.phone ?? ''].some((text) =>
          text.toLowerCase().includes(needle),
        ),
    )
    .sort((a, b) => b.lastActivityAt.localeCompare(a.lastActivityAt));

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Customers"
        description="Everyone who has asked about a trip, been quoted, or booked, with everything since."
      />

      <div className="max-w-md">
        <Input
          type="search"
          label="Search"
          placeholder="Name, email or phone"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
        />
      </div>

      {customers.isPending ? <LoadingState label="Loading your customers" /> : null}
      {customers.isError ? (
        <ErrorState
          {...describeError(customers.error)}
          onRetry={() => void customers.refetch()}
          retrying={customers.isFetching}
        />
      ) : null}

      {customers.data && visible.length === 0 ? (
        <EmptyState icon={<Users aria-hidden="true" className="h-5 w-5" />} title="Nobody matches">
          Customers appear here the first time they send a trip request or you add a lead.
        </EmptyState>
      ) : null}

      {visible.length > 0 ? (
        <Card className="overflow-hidden p-0">
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Customer</TableHead>
                  <TableHead className="text-right">Spent</TableHead>
                  <TableHead>Bookings</TableHead>
                  <TableHead>Open leads</TableHead>
                  <TableHead>Last heard from</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {visible.map((customer) => (
                  <TableRow key={customer.id}>
                    <TableCell>
                      <Link
                        to={`/crm/customers/${customer.id}`}
                        className="font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                      >
                        {customer.name}
                      </Link>
                      <p className="text-xs text-muted-foreground">
                        {[customer.email, customer.phone].filter(Boolean).join(' · ')}
                      </p>
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {customer.lifetimeValueMinor > 0
                        ? formatMoneyShort(customer.lifetimeValueMinor, 'NGN')
                        : '—'}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {customer.totalBookings || '—'}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {customer.openLeadCount || '—'}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {relativeTime(customer.lastActivityAt)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </Card>
      ) : null}
    </div>
  );
}
