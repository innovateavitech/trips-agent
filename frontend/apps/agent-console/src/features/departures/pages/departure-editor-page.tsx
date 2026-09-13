import { useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Card, ErrorState, LoadingState, Select } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useProducts } from '../../catalog/catalog-api';
import { draftFromDeparture, emptyDepartureDraft } from '../departure-rules';
import { useDeparture, useSaveDeparture } from '../departures-api';
import { DepartureForm } from '../components/departure-form';

/**
 * Build plan F6 — `/departures/new?product=…` adds a departure to a tour or
 * package; `/departures/:id/edit` changes one. Both land on the departure.
 */
export function DepartureEditorPage() {
  const { departureId } = useParams();
  const [params] = useSearchParams();

  return departureId ? (
    <EditDeparture id={departureId} />
  ) : (
    <NewDeparture preset={params.get('product')} />
  );
}

function NewDeparture({ preset }: { preset: string | null }) {
  const products = useProducts();
  const save = useSaveDeparture();
  const navigate = useNavigate();
  const [productId, setProductId] = useState(preset ?? '');

  // Visas have no departures; archived products are not sold.
  const choices = (products.data ?? []).filter(
    (product) => product.productType !== 'Visa' && product.status !== 'Archived',
  );
  const product = choices.find((choice) => choice.id === productId);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="New departure"
        description={product ? product.title : 'Choose the tour or package it is a run of.'}
      />

      {products.isPending ? <LoadingState label="Loading your catalog" /> : null}

      {products.data && !product ? (
        <Card className="p-5">
          <Select
            label="Tour or package"
            value={productId}
            onChange={(event) => setProductId(event.target.value)}
          >
            <option value="">Choose one</option>
            {choices.map((choice) => (
              <option key={choice.id} value={choice.id}>
                {choice.title || 'Untitled'}
              </option>
            ))}
          </Select>
        </Card>
      ) : null}

      {save.isError ? (
        <ErrorState title="We could not create it" detail={describeError(save.error).detail} />
      ) : null}

      {product ? (
        <DepartureForm
          key={product.id}
          currency={product.currency}
          initial={emptyDepartureDraft(product.basePriceMinor)}
          saving={save.isPending}
          submitLabel="Create departure"
          onSubmit={(request) =>
            save.mutate(
              { productId: product.id, existing: null, request },
              { onSuccess: (created) => navigate(`/departures/${created.id}`, { replace: true }) },
            )
          }
        />
      ) : null}
    </div>
  );
}

function EditDeparture({ id }: { id: string }) {
  const departure = useDeparture(id);
  const save = useSaveDeparture();
  const navigate = useNavigate();

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

  const current = departure.data;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title="Edit departure" description={current.productTitle} />

      {save.isError ? (
        <ErrorState title="We could not save it" detail={describeError(save.error).detail} />
      ) : null}

      <DepartureForm
        key={`${current.id}-${current.version}`}
        currency={current.currency}
        initial={draftFromDeparture(current)}
        saving={save.isPending}
        submitLabel="Save departure"
        onSubmit={(request) =>
          save.mutate(
            {
              productId: current.productId,
              existing: { id: current.id, version: current.version },
              request,
            },
            { onSuccess: () => navigate(`/departures/${current.id}`) },
          )
        }
      />
    </div>
  );
}
