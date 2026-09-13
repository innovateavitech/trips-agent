import { Badge, Button, Card, Skeleton } from '@trips/ui';
import { formatDateTime, formatMoney } from '../../../lib/format';
import {
  currentPrice,
  deletability,
  describeEntitlementValue,
  grantsOf,
  publishability,
  tierStatusDisplay,
} from '../billing-rules';
import type { EntitlementCatalogueItem, Tier } from '../types';

/** What an admin can do to a plan from its card. */
export type TierAction =
  'edit' | 'price' | 'entitlements' | 'publish' | 'archive' | 'restore' | 'delete' | 'migrate';

/**
 * One plan: what it costs, what it unlocks, who is on it, and what can be done to it.
 *
 * A card rather than a table row. A plan is four things at once — a price, a list of entitlements,
 * a status and a subscriber count — and a row wide enough to hold all four is a row nobody reads.
 */
export function TierCard({
  tier,
  catalogue,
  onAction,
}: {
  tier: Tier;
  catalogue: EntitlementCatalogueItem[];
  onAction: (tier: Tier, action: TierAction) => void;
}) {
  const status = tierStatusDisplay(tier.status);
  const price = currentPrice(tier);
  const canPublish = publishability(tier);
  const canDelete = deletability(tier);
  const grants = grantsOf(tier, catalogue);

  return (
    <Card className="flex flex-col gap-4 p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-base font-semibold text-foreground">{tier.name}</h2>
            <Badge tone={status.tone}>{status.label}</Badge>
            {tier.isFallback ? <Badge tone="info">Fallback plan</Badge> : null}
          </div>

          <p className="text-xs text-muted-foreground">
            <code className="font-mono">{tier.code}</code> · {status.meaning}
          </p>

          {tier.customerDescription ? (
            <p className="text-sm text-muted-foreground">{tier.customerDescription}</p>
          ) : null}
        </div>

        <div className="text-right">
          <p className="text-lg font-semibold text-foreground">
            {price ? formatMoney(price.amountMinor) : 'No price'}
          </p>
          <p className="text-xs text-muted-foreground">
            {price ? 'per month' : 'cannot be charged for'}
          </p>
        </div>
      </div>

      <dl className="grid gap-3 sm:grid-cols-3">
        <Fact label="Agencies on it" value={String(tier.subscribers)} />
        <Fact label="Free trial" value={tier.trialDays === 0 ? 'None' : `${tier.trialDays} days`} />
        <Fact
          label="Published"
          value={tier.publishedAt ? formatDateTime(tier.publishedAt) : 'Not yet'}
        />
      </dl>

      <div className="flex flex-col gap-2 rounded-md bg-muted p-3">
        <p className="text-xs font-medium text-foreground">What it includes</p>
        <ul className="grid gap-1 sm:grid-cols-2">
          {grants.map((grant) => (
            <li key={grant.code} className="flex items-baseline justify-between gap-2 text-xs">
              <span className="text-muted-foreground">{grant.name}</span>
              <span
                className={grant.granted ? 'font-medium text-foreground' : 'text-muted-foreground'}
              >
                {describeEntitlementValue(grant.valueType, grant.value)}
              </span>
            </li>
          ))}
        </ul>
        <p className="text-xs text-muted-foreground">
          Anything this plan does not set falls back to the platform default, which is always the
          more restrictive answer.
        </p>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" onClick={() => onAction(tier, 'edit')}>
          Edit
        </Button>
        <Button size="sm" variant="outline" onClick={() => onAction(tier, 'price')}>
          Set the price
        </Button>
        <Button size="sm" variant="outline" onClick={() => onAction(tier, 'entitlements')}>
          What it includes
        </Button>

        {tier.status !== 'Published' && tier.status !== 'Archived' ? (
          <Button
            size="sm"
            disabled={!canPublish.canPublish}
            title={canPublish.reason}
            onClick={() => onAction(tier, 'publish')}
          >
            Publish
          </Button>
        ) : null}

        {tier.status === 'Archived' ? (
          <Button size="sm" variant="outline" onClick={() => onAction(tier, 'restore')}>
            Restore
          </Button>
        ) : (
          <Button size="sm" variant="outline" onClick={() => onAction(tier, 'archive')}>
            Archive
          </Button>
        )}

        {tier.subscribers > 0 ? (
          <Button size="sm" variant="outline" onClick={() => onAction(tier, 'migrate')}>
            Move everyone off
          </Button>
        ) : null}

        {canDelete.canDelete ? (
          <Button size="sm" variant="destructive" onClick={() => onAction(tier, 'delete')}>
            Delete
          </Button>
        ) : null}
      </div>

      {canDelete.canDelete ? null : (
        <p className="text-xs text-muted-foreground">{canDelete.reason}</p>
      )}
    </Card>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="text-sm font-medium text-foreground">{value}</dd>
    </div>
  );
}

export function TierCardSkeleton() {
  return (
    <Card className="flex flex-col gap-4 p-5" aria-busy="true">
      <Skeleton className="h-5 w-40" />
      <Skeleton className="h-4 w-64" />
      <Skeleton className="h-16 w-full" />
      <Skeleton className="h-8 w-72" />
    </Card>
  );
}
