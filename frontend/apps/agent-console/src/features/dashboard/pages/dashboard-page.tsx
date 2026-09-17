import { useState, type ComponentType, type ReactNode } from 'react';
import { Ticket } from 'lucide-react';
import { Link } from 'react-router-dom';
import {
  AddIcon,
  AirplaneIcon,
  Badge,
  BookTravelIcon,
  BusIcon,
  CatalogueIcon,
  ChevronDownIcon,
  CircularArrowDownIcon,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  ErrorState,
  IconChip,
  ProgressBar,
  SegmentedControl,
  Skeleton,
  StatCard,
  TrendUpIcon,
  buttonVariants,
  type IconChipProps,
} from '@trips/ui';
import { formatMoney, formatMoneyParts } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { displayNameFor } from '../../../auth/auth-api';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { useWalletSummary } from '../../wallet';
import { useDashboardOverview } from '../dashboard-api';
import { greetingFor } from '../booking-display';
import type {
  EarningsSummary,
  InsightItem,
  InsightTone,
  TransactionRow,
  TransactionStatus,
  UpcomingItem,
} from '../types';

/**
 * Home. What the agent can do right now (book, create), what they can spend,
 * how the last 90 days went, what is coming up, what needs a reply today, and
 * what is owed to them — in that order, top to bottom, left to right.
 */
export function DashboardPage() {
  const user = useCurrentUser();
  const overview = useDashboardOverview();
  const now = new Date();

  return (
    <>
      <PageHeader
        title={`${greetingFor(now)}, ${displayNameFor(user).split(' ')[0]}`}
        actions={
          <>
            <Link to="/search/flights" className={buttonVariants({ size: 'md', radius: 'lg' })}>
              <BookTravelIcon size={16} />
              Book travel
            </Link>
            <DropdownMenu>
              <DropdownMenuTrigger
                className={buttonVariants({ variant: 'secondary', size: 'md', radius: 'lg' })}
              >
                Create
                <ChevronDownIcon size={16} />
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem asChild>
                  <Link to="/catalog">Tour or package</Link>
                </DropdownMenuItem>
                <DropdownMenuItem asChild>
                  <Link to="/crm/leads">Lead</Link>
                </DropdownMenuItem>
                <DropdownMenuItem asChild>
                  <Link to="/invoices">Invoice</Link>
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        }
      />

      {/*
        Two independent columns, each summing to the same total height —
        that's what makes them line up, not a shared CSS grid row: wallet
        (188) + a 10px gap + earnings (194) = 392, exactly matching "For you
        today" alone; then a 20px gap before the second 392px row on both
        sides. Change one side's arithmetic and the other must follow, or
        the columns drift apart again.
      */}
      <div className="grid gap-5 lg:grid-cols-2">
        <div className="flex flex-col gap-5">
          <div className="flex flex-col gap-2.5">
            <WalletCard />
            <EarningsCard
              isPending={overview.isPending}
              isError={overview.isError}
              data={overview.data?.home.earnings}
            />
          </div>
          <UpcomingItemsCard
            isPending={overview.isPending}
            isError={overview.isError}
            error={overview.error}
            onRetry={() => void overview.refetch()}
            items={overview.data?.home.upcomingItems}
          />
        </div>

        <div className="flex flex-col gap-5">
          <ForYouTodayCard
            isPending={overview.isPending}
            isError={overview.isError}
            error={overview.error}
            onRetry={() => void overview.refetch()}
            items={overview.data?.home.forYouToday}
          />
          <TransactionsCard
            isPending={overview.isPending}
            isError={overview.isError}
            invoices={overview.data?.home.invoices}
            payments={overview.data?.home.payments}
          />
        </div>
      </div>
    </>
  );
}

/** The header-row "View all" link + count bubble shared by Upcoming items and Transactions. */
function ViewAllLink({ to, count }: { to: string; count: number }) {
  return (
    <Link to={to} className="flex shrink-0 items-center gap-1.5">
      <span className="text-base font-semibold text-foreground underline">View all</span>
      <span className="flex size-5 items-center justify-center rounded-full bg-background text-xs font-semibold text-foreground">
        {count}
      </span>
    </Link>
  );
}

function CircleIconButton({
  to,
  label,
  children,
}: {
  to: string;
  label: string;
  children: ReactNode;
}) {
  return (
    <Link
      to={to}
      aria-label={label}
      className="flex size-10 items-center justify-center rounded-full border border-border-subtle bg-card hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {children}
    </Link>
  );
}

function WalletCard() {
  const summary = useWalletSummary();

  if (summary.isPending) {
    return (
      <div
        className="h-card-sm rounded-[2rem] bg-card p-6"
        aria-busy="true"
        aria-label="Loading your wallet balance"
      >
        <Skeleton className="mb-2 h-4 w-28" />
        <Skeleton className="h-9 w-56" />
      </div>
    );
  }

  if (summary.isError || !summary.data) {
    return (
      <div className="h-card-sm rounded-[2rem] bg-card p-6">
        <ErrorState
          title="We could not load your balance"
          detail="Your money is safe; this is a problem reading it."
          onRetry={() => void summary.refetch()}
        />
      </div>
    );
  }

  const { whole, fraction } = formatMoneyParts(summary.data.balanceMinor, summary.data.currency);
  const reservedShare =
    summary.data.balanceMinor > 0
      ? (summary.data.reservedMinor / summary.data.balanceMinor) * 100
      : 0;

  return (
    <StatCard
      className="h-card-sm"
      label="Wallet balance"
      value={whole}
      valueSuffix={fraction}
      caption={`Reserved balance: ${formatMoney(summary.data.reservedMinor, summary.data.currency)}`}
      actions={
        <>
          <CircleIconButton to="/wallet" label="Top up wallet">
            <AddIcon size={20} />
          </CircleIconButton>
          <CircleIconButton to="/payouts" label="Withdraw from wallet">
            <CircularArrowDownIcon size={20} />
          </CircleIconButton>
        </>
      }
    >
      <ProgressBar
        segments={[{ value: reservedShare, colorClassName: 'bg-chart-1', label: 'Reserved' }]}
      />
    </StatCard>
  );
}

function EarningsCard({
  isPending,
  isError,
  data,
}: {
  isPending: boolean;
  isError: boolean;
  data?: EarningsSummary;
}) {
  if (isPending || isError || !data) {
    return (
      <div className="h-card-md rounded-[2rem] bg-card p-6" aria-busy={isPending}>
        <Skeleton className="mb-2 h-4 w-36" />
        <Skeleton className="h-9 w-56" />
      </div>
    );
  }

  const { whole, fraction } = formatMoneyParts(data.totalMinor, data.currency);
  const changePct = (data.changeBasisPoints / 100).toFixed(0);
  const segmentColors = ['bg-chart-1', 'bg-chart-2'];

  return (
    <StatCard
      className="h-card-md"
      label="Total earnings, 90 days"
      value={whole}
      valueSuffix={fraction}
      caption={`+${changePct}%`}
      actions={
        <CircleIconButton to="/analytics" label="View earnings detail">
          <ChevronDownIcon size={20} className="-rotate-90" />
        </CircleIconButton>
      }
    >
      <ProgressBar
        segments={data.composition.map((part, index) => ({
          value: (part.valueMinor / data.totalMinor) * 100,
          colorClassName: segmentColors[index % segmentColors.length] ?? 'bg-muted',
          label: part.label,
        }))}
      />
    </StatCard>
  );
}

type RowIcon = ComponentType<{ size?: number; className?: string }>;

const UPCOMING_ICON: Record<UpcomingItem['product'], RowIcon> = {
  flight: AirplaneIcon,
  bus: BusIcon,
  tour: CatalogueIcon,
};

const UPCOMING_TONE: Record<UpcomingItem['product'], IconChipProps['tone']> = {
  flight: 'info',
  bus: 'warning',
  tour: 'success',
};

function UpcomingItemsCard({
  isPending,
  isError,
  error,
  onRetry,
  items,
}: {
  isPending: boolean;
  isError: boolean;
  error?: unknown;
  onRetry: () => void;
  items?: UpcomingItem[];
}) {
  const [tab, setTab] = useState<UpcomingItem['kind']>('travel');
  const filtered = items?.filter((item) => item.kind === tab) ?? [];

  return (
    <div className="flex h-card-lg flex-col rounded-[2rem] bg-card p-5">
      <div className="mb-4 flex shrink-0 items-center justify-between gap-4">
        <h2 className="text-base font-semibold text-foreground">
          Upcoming items{items ? ` (${items.length})` : ''}
        </h2>
        <ViewAllLink to="/bookings" count={items?.length ?? 0} />
      </div>

      <SegmentedControl
        label="Filter upcoming items"
        appearance="pill"
        value={tab}
        onChange={setTab}
        className="mb-5 shrink-0"
        options={[
          { value: 'travel', label: 'Travel' },
          { value: 'catalogue', label: 'Catalogue' },
        ]}
      />

      <div className="min-h-0 flex-1 overflow-y-auto">
        {isPending ? (
          <div className="flex flex-col gap-4" aria-busy="true" aria-label="Loading upcoming items">
            {[0, 1, 2].map((row) => (
              <Skeleton key={row} className="h-11 w-full" />
            ))}
          </div>
        ) : null}

        {isError ? <ErrorState {...describeError(error)} onRetry={onRetry} /> : null}

        {!isPending && !isError && filtered.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">Nothing here yet.</p>
        ) : null}

        {!isPending && !isError && filtered.length > 0 ? (
          <ul className="flex flex-col gap-6">
            {filtered.map((item) => {
              const Icon = UPCOMING_ICON[item.product];
              return (
                <li key={item.id}>
                  <Link to={item.href} className="flex items-center justify-between gap-3">
                    <span className="flex min-w-0 items-center gap-3">
                      <IconChip tone={UPCOMING_TONE[item.product]}>
                        <Icon size={20} />
                      </IconChip>
                      <span className="flex min-w-0 flex-col">
                        <span className="truncate text-base font-medium text-foreground">
                          {item.title}
                        </span>
                        <span className="truncate text-sm text-muted-foreground">
                          {item.meta.join(' · ')}
                        </span>
                      </span>
                    </span>
                    <ChevronDownIcon
                      size={16}
                      className="shrink-0 -rotate-90 text-muted-foreground"
                    />
                  </Link>
                </li>
              );
            })}
          </ul>
        ) : null}
      </div>
    </div>
  );
}

const INSIGHT_ICON: Record<InsightTone, RowIcon> = {
  destructive: Ticket,
  warning: Ticket,
  info: TrendUpIcon,
};

const INSIGHT_TONE: Record<InsightTone, IconChipProps['tone']> = {
  destructive: 'destructive',
  warning: 'warning',
  info: 'info',
};

function ForYouTodayCard({
  isPending,
  isError,
  error,
  onRetry,
  items,
}: {
  isPending: boolean;
  isError: boolean;
  error?: unknown;
  onRetry: () => void;
  items?: InsightItem[];
}) {
  return (
    <div className="flex h-card-lg flex-col rounded-[2rem] bg-card p-5">
      <h2 className="mb-4 shrink-0 text-base font-semibold text-foreground">
        For you today{items ? ` (${items.length})` : ''}
      </h2>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {isPending ? (
          <div
            className="flex flex-col gap-4"
            aria-busy="true"
            aria-label="Loading today's insights"
          >
            {[0, 1, 2].map((row) => (
              <Skeleton key={row} className="h-16 w-full" />
            ))}
          </div>
        ) : null}

        {isError ? <ErrorState {...describeError(error)} onRetry={onRetry} /> : null}

        {!isPending && !isError && items && items.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">
            Nothing needs you right now.
          </p>
        ) : null}

        {!isPending && !isError && items && items.length > 0 ? (
          <ul className="flex flex-col gap-6">
            {items.map((item) => {
              const Icon = INSIGHT_ICON[item.tone];
              return (
                <li key={item.id} className="flex items-start gap-3">
                  <IconChip tone={INSIGHT_TONE[item.tone]} className="mt-0.5">
                    <Icon size={20} />
                  </IconChip>
                  <div className="flex flex-1 flex-col gap-2.5">
                    <p className="text-base text-foreground">{item.message}</p>
                    <div className="flex flex-wrap gap-2">
                      {item.actions.map((action) => (
                        <Link
                          key={action.label}
                          to={action.href}
                          className={buttonVariants({ variant: 'outline', size: 'sm' })}
                        >
                          {action.label}
                        </Link>
                      ))}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        ) : null}
      </div>
    </div>
  );
}

const TRANSACTION_TONE: Record<
  TransactionStatus,
  { label: string; tone: 'destructive' | 'warning' | 'neutral' | 'success' }
> = {
  overdue: { label: 'Overdue', tone: 'destructive' },
  pending: { label: 'Pending', tone: 'warning' },
  draft: { label: 'Draft', tone: 'neutral' },
  paid: { label: 'Paid', tone: 'success' },
};

function TransactionsCard({
  isPending,
  isError,
  invoices,
  payments,
}: {
  isPending: boolean;
  isError: boolean;
  invoices?: TransactionRow[];
  payments?: TransactionRow[];
}) {
  const [tab, setTab] = useState<'invoices' | 'payments'>('invoices');
  const rows = (tab === 'invoices' ? invoices : payments) ?? [];

  return (
    <div className="flex h-card-lg flex-col rounded-[2rem] bg-card p-5">
      <div className="mb-4 flex shrink-0 items-center justify-between gap-4">
        <h2 className="text-base font-semibold text-foreground">Transactions</h2>
        <ViewAllLink to="/invoices" count={rows.length} />
      </div>

      <SegmentedControl
        label="Filter transactions"
        appearance="pill"
        value={tab}
        onChange={setTab}
        className="mb-5 shrink-0"
        options={[
          { value: 'invoices', label: 'Invoices' },
          { value: 'payments', label: 'Payments' },
        ]}
      />

      <div className="min-h-0 flex-1 overflow-y-auto">
        {isPending ? (
          <div className="flex flex-col gap-4" aria-busy="true" aria-label="Loading transactions">
            {[0, 1, 2].map((row) => (
              <Skeleton key={row} className="h-11 w-full" />
            ))}
          </div>
        ) : null}

        {!isPending && !isError && rows.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">Nothing here yet.</p>
        ) : null}

        {!isPending && !isError && rows.length > 0 ? (
          <ul className="flex flex-col gap-6">
            {rows.map((row) => {
              const display = TRANSACTION_TONE[row.status];
              return (
                <li key={row.id}>
                  <Link to={row.href} className="flex items-center justify-between gap-3">
                    <span className="flex min-w-0 flex-col">
                      <span className="truncate text-base font-medium text-foreground">
                        {row.name}
                      </span>
                      <span className="truncate text-sm text-muted-foreground">
                        {formatMoney(row.amountMinor, row.currency)} · {row.reference}
                      </span>
                    </span>
                    <Badge tone={display.tone} className="shrink-0">
                      {display.label}
                    </Badge>
                  </Link>
                </li>
              );
            })}
          </ul>
        ) : null}
      </div>
    </div>
  );
}
