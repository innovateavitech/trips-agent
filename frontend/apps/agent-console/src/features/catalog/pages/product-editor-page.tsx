import { ArrowLeft, Compass } from 'lucide-react';
import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  Input,
  LoadingState,
  Textarea,
  buttonVariants,
} from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { useProduct, useProductTransition, useSaveProduct } from '../catalog-api';
import {
  buildRequest,
  canEditCatalog,
  canPublishCatalog,
  countryName,
  draftFromProduct,
  emptyDraft,
  sameDraft,
  sectionOf,
  singularOf,
  typeFromSlug,
  type EditorSection as SectionId,
  type FieldErrors,
  type ProductDraft,
} from '../catalog-rules';
import { CategoryPicker } from '../components/category-picker';
import { EditorSection, ProductStatusBadge } from '../components/editor-parts';
import { ImagePicker } from '../components/image-picker';
import { InclusionsEditor } from '../components/inclusions-editor';
import { ItineraryBuilder } from '../components/itinerary-builder';
import { PriceVariantsEditor } from '../components/price-variants-editor';
import { PublishChecklist } from '../components/publish-checklist';
import { TransitionDialog } from '../components/transition-dialog';
import { VisaEditor } from '../components/visa-editor';
import type { Product, ProductTransition } from '../types';

/**
 * issue 162 and issue 163 — one editor for tours, packages and visas. `/catalog/new/tour`
 * starts a draft; the first save creates it and moves to `/catalog/:id`.
 */
export function ProductEditorPage() {
  const { productId, type } = useParams();

  if (productId) return <ExistingProduct id={productId} />;

  const productType = typeFromSlug(type);
  if (!productType) {
    return (
      <EmptyState
        size="page"
        headingLevel={1}
        icon={<Compass aria-hidden="true" className="h-5 w-5" />}
        title="There is no such kind of product"
        action={
          <Link to="/catalog" className={buttonVariants({ size: 'sm' })}>
            Back to the catalog
          </Link>
        }
      >
        The catalog holds tours, packages and visas.
      </EmptyState>
    );
  }

  return (
    <ProductEditor key={`new-${productType}`} product={null} initial={emptyDraft(productType)} />
  );
}

function ExistingProduct({ id }: { id: string }) {
  const product = useProduct(id);

  if (product.isPending) return <LoadingState label="Opening the product" />;
  if (product.isError) {
    return (
      <ErrorState
        {...describeError(product.error)}
        onRetry={() => void product.refetch()}
        retrying={product.isFetching}
      />
    );
  }

  return (
    <ProductEditor
      key={product.data.id}
      product={product.data}
      initial={draftFromProduct(product.data)}
    />
  );
}

function ProductEditor({ product, initial }: { product: Product | null; initial: ProductDraft }) {
  const user = useCurrentUser();
  const navigate = useNavigate();
  const save = useSaveProduct();
  const transition = useProductTransition(product?.id ?? '');

  const [baseline, setBaseline] = useState(initial);
  const [draft, setDraft] = useState(initial);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [confirming, setConfirming] = useState<ProductTransition | null>(null);

  const mayEdit = canEditCatalog(user.roles);
  const archived = product?.status === 'Archived';
  const canEdit = mayEdit && !archived;
  const canPublish = canPublishCatalog(user.roles);
  const dirty = !sameDraft(draft, baseline);
  const currency = product?.currency ?? 'NGN';
  const problems = product?.publishProblems ?? [];
  const isVisa = draft.productType === 'Visa';
  const noun = singularOf(draft.productType);
  const problemsIn = (section: SectionId) =>
    problems.filter((problem) => sectionOf(problem) === section);

  /** Applies a change, and forgets the errors about the fields it touched. */
  function update(patch: Partial<ProductDraft>) {
    setDraft((current) => ({ ...current, ...patch }));
    const touched = Object.keys(patch);
    setErrors((current) =>
      Object.fromEntries(
        Object.entries(current).filter(
          ([field]) => !touched.some((key) => field === key || field.startsWith(`${key}.`)),
        ),
      ),
    );
  }

  function onSave() {
    const built = buildRequest(draft, currency);
    if (!built.ok) {
      setErrors(built.errors);
      return;
    }

    save.mutate(
      { id: product?.id ?? null, request: built.request },
      {
        onSuccess: (saved) => {
          const next = draftFromProduct(saved);
          setBaseline(next);
          setDraft(next);
          setErrors({});
          if (!product) navigate(`/catalog/${saved.id}`, { replace: true });
        },
      },
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to="/catalog"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          Catalog
        </Link>
      </div>

      <PageHeader
        title={draft.title.trim() || (product ? 'Untitled' : `New ${noun}`)}
        description={
          product
            ? `${draft.productType} · /${product.slug}`
            : `A ${noun}, saved as a draft until you publish it.`
        }
        actions={
          <div className="flex flex-wrap items-center gap-2">
            {product ? (
              <ProductStatusBadge status={product.status} />
            ) : (
              <Badge tone="neutral">Not saved</Badge>
            )}
            {dirty ? <span className="text-xs text-muted-foreground">Unsaved changes</span> : null}
            {product && product.productType !== 'Visa' ? (
              <Link
                to={`/departures?product=${product.id}`}
                className={buttonVariants({ variant: 'outline' })}
              >
                Departures
              </Link>
            ) : null}
            {canPublish && product?.status === 'Draft' ? (
              <Button
                variant="outline"
                onClick={() => setConfirming('publish')}
                disabled={dirty || problems.length > 0}
              >
                Publish
              </Button>
            ) : null}
            {canPublish && product?.status === 'Published' ? (
              <Button variant="outline" onClick={() => setConfirming('unpublish')}>
                Unpublish
              </Button>
            ) : null}
            {canPublish && archived ? (
              <Button variant="outline" onClick={() => setConfirming('unpublish')}>
                Restore as draft
              </Button>
            ) : null}
            {canEdit ? (
              <Button
                onClick={onSave}
                loading={save.isPending}
                disabled={product !== null && !dirty}
              >
                {product ? 'Save' : 'Save draft'}
              </Button>
            ) : null}
          </div>
        }
      />

      {!mayEdit ? (
        <Alert tone="info" title="You can look, but not change">
          Changing the catalog needs an owner or a manager of the agency.
        </Alert>
      ) : archived ? (
        <Alert tone="info" title="Archived products cannot be changed">
          Restore it as a draft to edit it or publish it again.
        </Alert>
      ) : null}

      {save.isError ? (
        <ErrorState title="We could not save it" detail={describeError(save.error).detail} />
      ) : null}
      {transition.isError ? (
        <ErrorState title="That did not work" detail={describeError(transition.error).detail} />
      ) : null}

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <fieldset disabled={!canEdit} className="flex min-w-0 flex-col gap-6 lg:col-span-2">
          <legend className="sr-only">{`${noun} details`}</legend>

          <EditorSection id="basics" problems={problemsIn('basics')}>
            <Input
              label="Title"
              value={draft.title}
              onChange={(event) => update({ title: event.target.value })}
            />
            <Input
              label="Summary"
              hint="One line for listings and search results"
              value={draft.summary}
              onChange={(event) => update({ summary: event.target.value })}
            />
            <Textarea
              label="Description"
              rows={5}
              value={draft.description}
              onChange={(event) => update({ description: event.target.value })}
            />
            <div className="grid items-start gap-3 sm:grid-cols-2">
              <Input
                label="City"
                value={draft.destinationCity}
                onChange={(event) => update({ destinationCity: event.target.value })}
              />
              <Input
                label="Country"
                hint={countryName(draft.destinationCountry) || 'Two letters, like NG or AE'}
                maxLength={2}
                value={draft.destinationCountry}
                onChange={(event) =>
                  update({ destinationCountry: event.target.value.toUpperCase() })
                }
              />
              <Input
                label={`Price from (${currency})`}
                hint={isVisa ? 'Usually the fees together' : 'The lowest price a customer can pay'}
                inputMode="decimal"
                value={draft.basePrice}
                onChange={(event) => update({ basePrice: event.target.value })}
                error={errors['basePrice']}
              />
              {isVisa ? null : (
                <Input
                  label="Length (days)"
                  inputMode="numeric"
                  value={draft.durationDays}
                  onChange={(event) => update({ durationDays: event.target.value })}
                  error={errors['durationDays']}
                />
              )}
              {isVisa ? null : (
                <>
                  <Input
                    type="date"
                    label="Bookable from"
                    hint="Empty means now"
                    value={draft.availableFrom}
                    onChange={(event) => update({ availableFrom: event.target.value })}
                  />
                  <Input
                    type="date"
                    label="Bookable until"
                    value={draft.availableTo}
                    onChange={(event) => update({ availableTo: event.target.value })}
                  />
                </>
              )}
            </div>
          </EditorSection>

          <EditorSection
            id="images"
            description="The cover leads on your storefront."
            problems={problemsIn('images')}
          >
            <ImagePicker
              media={draft.media}
              heroAssetId={draft.heroAssetId}
              onAdd={(image) =>
                setDraft((current) => ({
                  ...current,
                  media: [...current.media, image],
                  heroAssetId: current.heroAssetId ?? image.assetId,
                }))
              }
              onRemove={(assetId) =>
                setDraft((current) => ({
                  ...current,
                  media: current.media.filter((item) => item.assetId !== assetId),
                  heroAssetId: current.heroAssetId === assetId ? null : current.heroAssetId,
                }))
              }
              onCaption={(assetId, caption) =>
                setDraft((current) => ({
                  ...current,
                  media: current.media.map((item) =>
                    item.assetId === assetId ? { ...item, caption } : item,
                  ),
                }))
              }
              onCover={(assetId) => update({ heroAssetId: assetId })}
            />
          </EditorSection>

          {isVisa && draft.visa ? (
            <EditorSection id="visa" problems={problemsIn('visa')}>
              <VisaEditor
                visa={draft.visa}
                errors={errors}
                currency={currency}
                onChange={(visa) => update({ visa })}
              />
            </EditorSection>
          ) : null}

          {isVisa ? null : (
            <>
              <EditorSection
                id="itinerary"
                description="Day by day: what happens, which meals are included, where they sleep."
                problems={problemsIn('itinerary')}
              >
                <ItineraryBuilder
                  days={draft.itinerary}
                  onChange={(itinerary) => update({ itinerary })}
                />
              </EditorSection>

              <EditorSection id="inclusions" problems={problemsIn('inclusions')}>
                <InclusionsEditor
                  inclusions={draft.inclusions}
                  onChange={(inclusions) => update({ inclusions })}
                />
              </EditorSection>

              <EditorSection id="prices" problems={problemsIn('prices')}>
                <PriceVariantsEditor
                  variants={draft.variants}
                  errors={errors}
                  currency={currency}
                  onChange={(variants) => update({ variants })}
                />
              </EditorSection>
            </>
          )}

          <EditorSection
            id="categories"
            description="Your storefront filters by these."
            problems={problemsIn('categories')}
          >
            <CategoryPicker
              selected={draft.categoryIds}
              onChange={(categoryIds) => update({ categoryIds })}
              canCreate={canEdit}
            />
          </EditorSection>
        </fieldset>

        <aside className="flex flex-col gap-4 lg:sticky lg:top-6">
          <PublishChecklist product={product} dirty={dirty} />

          {canPublish && product && product.status !== 'Archived' ? (
            <Card className="flex flex-col gap-2 p-5">
              <h2 className="text-sm font-semibold text-foreground">Archive</h2>
              <p className="text-sm text-muted-foreground">
                Retire it from your storefront and this list. You can restore it later.
              </p>
              <div>
                <Button variant="outline" size="sm" onClick={() => setConfirming('archive')}>
                  Archive this {noun}
                </Button>
              </div>
            </Card>
          ) : null}
        </aside>
      </div>

      <TransitionDialog
        action={confirming}
        product={product}
        pending={transition.isPending}
        onClose={() => setConfirming(null)}
        onConfirm={() => {
          if (confirming) transition.mutate(confirming, { onSettled: () => setConfirming(null) });
        }}
      />
    </div>
  );
}
