import { useState } from 'react';
import { Alert, Button, Card } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { TierCard, TierCardSkeleton, type TierAction } from '../components/tier-card';
import {
  MigrateSubscribersDialog,
  TierActionDialog,
  TierEntitlementsDialog,
  TierFormDialog,
  TierPriceDialog,
} from '../components/tier-dialogs';
import {
  useCreateTier,
  useEntitlementCatalogue,
  useMigrateSubscribers,
  useSetTierEntitlements,
  useSetTierPrice,
  useTierLifecycle,
  useTiers,
  useUpdateTier,
} from '../billing-queries';
import type { Tier } from '../types';

/** Which plan is open, and which dialog is open on it. */
interface Open {
  tier: Tier;
  action: TierAction;
}

/**
 * The plan builder.
 *
 * Behind `subscription.manage`, which a Super Admin and Finance hold. What it is careful about is
 * the difference between archiving and deleting: archiving retires a plan and leaves everyone on
 * it exactly where they are, and it is the right answer almost every time somebody says "delete".
 */
export function TiersPage() {
  useDocumentTitle('Plans');

  const [includeArchived, setIncludeArchived] = useState(true);
  const [creating, setCreating] = useState(false);
  const [open, setOpen] = useState<Open | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const tiers = useTiers(includeArchived);
  const catalogue = useEntitlementCatalogue();

  const create = useCreateTier();
  const update = useUpdateTier(open?.tier.id ?? '');
  const price = useSetTierPrice(open?.tier.id ?? '');
  const entitlements = useSetTierEntitlements(open?.tier.id ?? '');
  const lifecycle = useTierLifecycle(open?.tier.id ?? '');
  const migrate = useMigrateSubscribers(open?.tier.id ?? '');

  const list = tiers.data ?? [];
  const items = catalogue.data ?? [];

  function close() {
    setOpen(null);
    update.reset();
    price.reset();
    entitlements.reset();
    lifecycle.reset();
    migrate.reset();
  }

  return (
    <Page wide>
      <PageHeader
        title="Plans"
        description="What Trips sells to its agencies: what each plan costs, what it unlocks, and who is on it."
        actions={
          <Button
            size="sm"
            onClick={() => {
              create.reset();
              setCreating(true);
            }}
          >
            Create a plan
          </Button>
        }
      />

      {done ? (
        <Alert tone="success" title="Done">
          {done}
        </Alert>
      ) : null}

      {tiers.isError ? (
        <ErrorState
          {...describeLoadError(tiers.error)}
          onRetry={() => void tiers.refetch()}
          retrying={tiers.isFetching}
        />
      ) : null}

      <label className="flex items-center gap-2 text-sm text-muted-foreground">
        <input
          type="checkbox"
          className="h-4 w-4 accent-primary"
          checked={includeArchived}
          onChange={(event) => setIncludeArchived(event.target.checked)}
        />
        Show retired plans
      </label>

      {tiers.isPending ? (
        <div className="flex flex-col gap-4">
          <TierCardSkeleton />
          <TierCardSkeleton />
        </div>
      ) : null}

      {tiers.data && list.length === 0 ? (
        <Card>
          <EmptyState title="There are no plans yet">
            Create one, give it a price and say what it includes, then publish it. Until a plan is
            published nobody can subscribe to it.
          </EmptyState>
        </Card>
      ) : null}

      {list.length > 0 ? (
        <div className="flex flex-col gap-4">
          {list.map((tier) => (
            <TierCard
              key={tier.id}
              tier={tier}
              catalogue={items}
              onAction={(chosen, action) => {
                setDone(null);
                close();
                setOpen({ tier: chosen, action });
              }}
            />
          ))}
        </div>
      ) : null}

      <TierFormDialog
        open={creating}
        onOpenChange={setCreating}
        onSubmit={(request) =>
          create.mutate(request, {
            onSuccess: (response) => {
              setCreating(false);
              setDone(
                `${response.tier.name} was created as a draft. Nobody can subscribe until you publish it.`,
              );
            },
          })
        }
        pending={create.isPending}
        error={create.error}
      />

      {open?.action === 'edit' ? (
        <TierFormDialog
          key={`${open.tier.id}-edit`}
          open
          onOpenChange={(next) => (next ? undefined : close())}
          tier={open.tier}
          onSubmit={(request) =>
            update.mutate(request, {
              onSuccess: () => {
                close();
                setDone(`${open.tier.name} was updated.`);
              },
            })
          }
          pending={update.isPending}
          error={update.error}
        />
      ) : null}

      {open?.action === 'price' ? (
        <TierPriceDialog
          key={`${open.tier.id}-price`}
          open
          onOpenChange={(next) => (next ? undefined : close())}
          tier={open.tier}
          onSubmit={(request) =>
            price.mutate(request, {
              onSuccess: () => {
                close();
                setDone(
                  `${open.tier.name} has a new price. Agencies already on it keep what they agreed — ` +
                    'move them separately if they should pay the new rate.',
                );
              },
            })
          }
          pending={price.isPending}
          error={price.error}
        />
      ) : null}

      {open?.action === 'entitlements' ? (
        <TierEntitlementsDialog
          key={`${open.tier.id}-entitlements`}
          open
          onOpenChange={(next) => (next ? undefined : close())}
          tier={open.tier}
          catalogue={items}
          onSubmit={(request) =>
            entitlements.mutate(request, {
              onSuccess: () => {
                close();
                setDone(
                  `What ${open.tier.name} includes was saved. It applies to everyone on it from now.`,
                );
              },
            })
          }
          pending={entitlements.isPending}
          error={entitlements.error}
        />
      ) : null}

      {open && LIFECYCLE[open.action] ? (
        <TierActionDialog
          key={`${open.tier.id}-${open.action}`}
          open
          onOpenChange={(next) => (next ? undefined : close())}
          title={LIFECYCLE[open.action]!.title(open.tier)}
          consequences={LIFECYCLE[open.action]!.consequences(open.tier)}
          confirmLabel={LIFECYCLE[open.action]!.confirmLabel}
          destructive={LIFECYCLE[open.action]!.destructive}
          onConfirm={(reason) =>
            lifecycle.mutate(
              { action: LIFECYCLE[open.action]!.verb, reason },
              {
                onSuccess: () => {
                  close();
                  setDone(LIFECYCLE[open.action]!.done(open.tier));
                },
              },
            )
          }
          pending={lifecycle.isPending}
          error={lifecycle.error}
        />
      ) : null}

      {open?.action === 'migrate' ? (
        <MigrateSubscribersDialog
          key={`${open.tier.id}-migrate`}
          open
          onOpenChange={(next) => (next ? undefined : close())}
          tier={open.tier}
          targets={list.filter(
            (candidate) => candidate.status === 'Published' && candidate.id !== open.tier.id,
          )}
          onSubmit={(request) =>
            migrate.mutate(request, {
              onSuccess: (response) => {
                close();
                setDone(
                  `${response.subscribersScheduled} ${
                    response.subscribersScheduled === 1 ? 'agency was' : 'agencies were'
                  } told, and move in thirty days. Nothing has changed for them yet.`,
                );
              },
            })
          }
          pending={migrate.isPending}
          error={migrate.error}
        />
      ) : null}
    </Page>
  );
}

/**
 * The four lifecycle actions, and what each one really does.
 *
 * The consequences are spelled out rather than left to the verb. "Archive" and "delete" sound alike
 * and are not: one retires a plan and keeps every invoice that names it readable, and the other is
 * only ever allowed on a draft nobody has ever been on.
 */
const LIFECYCLE: Partial<
  Record<
    TierAction,
    {
      verb: 'publish' | 'archive' | 'restore' | 'delete';
      title: (tier: Tier) => string;
      consequences: (tier: Tier) => string;
      confirmLabel: string;
      destructive?: boolean;
      done: (tier: Tier) => string;
    }
  >
> = {
  publish: {
    verb: 'publish',
    title: (tier) => `Publish ${tier.name}`,
    consequences: () => 'Agencies will see it in the plan picker and can subscribe to it.',
    confirmLabel: 'Publish it',
    done: (tier) => `${tier.name} is live. Agencies can subscribe to it now.`,
  },
  archive: {
    verb: 'archive',
    title: (tier) => `Archive ${tier.name}`,
    consequences: (tier) =>
      tier.subscribers > 0
        ? `It leaves the plan picker. The ${tier.subscribers} ${
            tier.subscribers === 1 ? 'agency' : 'agencies'
          } on it keep it and are not affected — move them separately if they should be.`
        : 'It leaves the plan picker. Nobody new can subscribe, and every invoice that names it stays readable.',
    confirmLabel: 'Archive it',
    done: (tier) => `${tier.name} was archived. Anyone on it keeps it; nobody new can join.`,
  },
  restore: {
    verb: 'restore',
    title: (tier) => `Restore ${tier.name}`,
    consequences: () => 'It goes back into the plan picker at whatever status it had before.',
    confirmLabel: 'Restore it',
    done: (tier) => `${tier.name} is back in the picker.`,
  },
  delete: {
    verb: 'delete',
    title: (tier) => `Delete ${tier.name}`,
    consequences: () =>
      'A draft nobody has ever been on, so nothing refers to it and nothing is lost. This cannot be undone.',
    confirmLabel: 'Delete it',
    destructive: true,
    done: (tier) => `${tier.name} was deleted.`,
  },
};
