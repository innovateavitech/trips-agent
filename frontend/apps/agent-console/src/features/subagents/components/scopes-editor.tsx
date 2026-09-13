import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  ErrorState,
  LoadingState,
  Select,
} from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useChangeScope, useSubAgentScopes } from '../subagents-api';
import type { SellableProductType } from '../types';

/**
 * What a sub-agent may sell.
 *
 * It fails closed: an empty list means it can sell nothing at all, which is
 * what a brand-new sub-agent looks like. The card says so plainly rather than
 * showing an empty table and letting somebody assume the opposite.
 *
 * Flights and buses are the two the platform can enforce today. The others are
 * catalog products and are offered here so a scope does not have to be revisited
 * when the catalog lands.
 */
const PRODUCT_TYPES: { value: SellableProductType; label: string; sellableNow: boolean }[] = [
  { value: 'Flight', label: 'Flights', sellableNow: true },
  { value: 'Bus', label: 'Bus tickets', sellableNow: true },
  { value: 'Tour', label: 'Tours', sellableNow: false },
  { value: 'Visa', label: 'Visas', sellableNow: false },
  { value: 'Package', label: 'Packages', sellableNow: false },
];

export function ScopesEditor({
  subAgencyId,
  readOnly,
}: {
  subAgencyId: string;
  readOnly: boolean;
}) {
  const scopes = useSubAgentScopes(subAgencyId);
  const change = useChangeScope(subAgencyId);
  const [adding, setAdding] = useState<SellableProductType>('Flight');

  if (scopes.isPending) {
    return <LoadingState label="Loading what they may sell" />;
  }

  if (scopes.isError) {
    const problem = describeError(scopes.error);

    return (
      <ErrorState
        title={problem.title}
        detail={problem.detail}
        onRetry={() => void scopes.refetch()}
        retrying={scopes.isFetching}
      />
    );
  }

  const granted = scopes.data;
  const alreadyGranted = new Set(granted.map((scope) => scope.productType));

  return (
    <Card>
      <CardHeader>
        <CardTitle>What they may sell</CardTitle>
        <CardDescription>
          Only what is listed here. A sub-agent with nothing listed can sell nothing — search shows
          them no results, and a booking sent straight to the API is refused.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-4">
        {change.isError ? (
          <Alert tone="destructive" title={describeError(change.error).title}>
            {describeError(change.error).detail}
          </Alert>
        ) : null}

        {granted.length === 0 ? (
          <Alert tone="warning" title="They cannot sell anything yet">
            Choose at least one product type below. Until you do, every search comes back empty and
            every booking is refused.
          </Alert>
        ) : (
          <ul className="flex flex-col divide-y divide-border">
            {granted.map((scope) => (
              <li key={scope.id} className="flex flex-wrap items-center gap-3 py-3">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium text-foreground">
                    {PRODUCT_TYPES.find((type) => type.value === scope.productType)?.label ??
                      scope.productType}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    {scope.supplierName === null
                      ? 'Every supplier'
                      : `Through ${scope.supplierName} only`}
                  </p>
                </div>

                {PRODUCT_TYPES.find((type) => type.value === scope.productType)?.sellableNow ===
                false ? (
                  <Badge tone="neutral">Not sellable yet</Badge>
                ) : null}

                {readOnly ? null : (
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={change.isPending}
                    onClick={() => change.mutate({ revoke: scope.id })}
                  >
                    Remove
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}

        {readOnly ? null : (
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              change.mutate({ grant: adding, supplierId: null });
            }}
          >
            <Select
              label="Add something they may sell"
              value={adding}
              onChange={(event) => setAdding(event.target.value as SellableProductType)}
            >
              {PRODUCT_TYPES.map((type) => (
                <option
                  key={type.value}
                  value={type.value}
                  disabled={alreadyGranted.has(type.value)}
                >
                  {type.label}
                  {alreadyGranted.has(type.value) ? ' — already added' : ''}
                </option>
              ))}
            </Select>

            <Button type="submit" disabled={change.isPending || alreadyGranted.has(adding)}>
              Add
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  );
}
