import { beforeEach, describe, expect, it } from 'vitest';
import {
  buildRequest,
  canEditCatalog,
  canPublishCatalog,
  countryName,
  draftFromProduct,
  emptyDraft,
  filterProducts,
  moveItem,
  newKey,
  sameDraft,
  sectionOf,
  statusCounts,
  typeFromSlug,
  visaTotalMinor,
  type ProductDraft,
} from '../catalog-rules';
import { findProduct, resetCatalogStore } from '../mock/catalog-store';
import { NO_PRODUCT_FILTERS, type Product, type ProductSummary } from '../types';

beforeEach(resetCatalogStore);

function stored(id: string): Product {
  const product = findProduct(id);
  if (!product) throw new Error(`no seed product ${id}`);
  return { ...product, publishProblems: [] };
}

function summary(patch: Partial<ProductSummary>): ProductSummary {
  return {
    id: 'p1',
    productType: 'Tour',
    title: 'Obudu weekend',
    slug: 'obudu-weekend',
    status: 'Published',
    destinationCity: 'Obudu',
    destinationCountry: 'NG',
    durationDays: 3,
    basePriceMinor: 100,
    currency: 'NGN',
    heroPreviewUrl: null,
    updatedAt: '2026-09-01T10:00:00Z',
    publishProblemCount: 0,
    ...patch,
  };
}

describe('who may do what', () => {
  it('lets owners and managers edit and publish, and agents only look', () => {
    expect(canEditCatalog(['Owner'])).toBe(true);
    expect(canPublishCatalog(['Manager'])).toBe(true);
    expect(canEditCatalog(['Agent'])).toBe(false);
    expect(canPublishCatalog(['Agent'])).toBe(false);
  });
});

describe('finding a product', () => {
  const products = [
    summary({}),
    summary({ id: 'p2', title: 'Zanzibar Beach Escape', productType: 'Package', destinationCity: 'Zanzibar', destinationCountry: 'TZ', status: 'Draft', updatedAt: '2026-09-05T10:00:00Z' }),
    summary({ id: 'p3', title: 'Dubai visa', productType: 'Visa', status: 'Archived', updatedAt: '2026-08-01T10:00:00Z' }),
  ];

  it('filters by type and status, newest change first', () => {
    expect(filterProducts(products, NO_PRODUCT_FILTERS).map((p) => p.id)).toEqual(['p2', 'p1', 'p3']);
    expect(filterProducts(products, { ...NO_PRODUCT_FILTERS, type: 'Visa' }).map((p) => p.id)).toEqual(['p3']);
    expect(filterProducts(products, { ...NO_PRODUCT_FILTERS, status: 'Draft' }).map((p) => p.id)).toEqual(['p2']);
  });

  it('searches the title, the city and the country by name', () => {
    expect(filterProducts(products, { ...NO_PRODUCT_FILTERS, query: 'tanzania' }).map((p) => p.id)).toEqual(['p2']);
    expect(filterProducts(products, { ...NO_PRODUCT_FILTERS, query: 'OBUDU' }).map((p) => p.id)).toEqual(['p1']);
  });

  it('counts each status', () => {
    expect(statusCounts(products)).toEqual({ all: 3, Draft: 1, Published: 1, Archived: 1 });
  });

  it('names countries, and falls back to the code', () => {
    expect(countryName('TZ')).toBe('Tanzania');
    expect(countryName('')).toBe('');
  });
});

describe('saving a draft', () => {
  it('saves an empty draft: nothing is required until publishing', () => {
    const built = buildRequest(emptyDraft('Tour'), 'NGN');

    expect(built.ok).toBe(true);
    if (!built.ok) return;
    expect(built.request.basePriceMinor).toBe(0);
    expect(built.request.durationDays).toBeNull();
    expect(built.request.availableTo).toBeNull();
    expect(built.request.visa).toBeNull();
  });

  it('turns typed amounts into kobo', () => {
    const built = buildRequest({ ...emptyDraft('Tour'), basePrice: '145,000.50' }, 'NGN');

    expect(built.ok && built.request.basePriceMinor).toBe(14_500_050);
  });

  it('refuses what is not a number, rather than lose what the agent meant', () => {
    const built = buildRequest({ ...emptyDraft('Tour'), basePrice: 'about 5k', durationDays: 'three' }, 'NGN');

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(Object.keys(built.errors).sort()).toEqual(['basePrice', 'durationDays']);
  });

  it('refuses a group that ends before it starts', () => {
    const draft = emptyDraft('Tour');
    const built = buildRequest(
      {
        ...draft,
        variants: [{ key: newKey(), name: 'Groups', paxType: 'Adult', occupancy: '', minGroupSize: '10', maxGroupSize: '4', price: '100' }],
      },
      'NGN',
    );

    expect(!built.ok && built.errors['variants.0.maxGroupSize']).toBeTruthy();
  });

  it('numbers itinerary days by position', () => {
    const draft: ProductDraft = {
      ...emptyDraft('Tour'),
      itinerary: ['Arrive', 'Explore', 'Leave'].map((title) => ({ key: newKey(), title, description: '', meals: [], accommodation: '' })),
    };
    const moved = { ...draft, itinerary: moveItem(draft.itinerary, 2, -1) };
    const built = buildRequest(moved, 'NGN');

    expect(built.ok && built.request.itinerary.map((day) => [day.dayNumber, day.title])).toEqual([
      [1, 'Arrive'],
      [2, 'Leave'],
      [3, 'Explore'],
    ]);
  });

  it('makes the first image the cover when none is chosen', () => {
    const built = buildRequest(
      { ...emptyDraft('Tour'), media: [{ assetId: 'a1', caption: ' Beach ', previewUrl: null }] },
      'NGN',
    );

    expect(built.ok && built.request.heroAssetId).toBe('a1');
    expect(built.ok && built.request.media).toEqual([{ assetId: 'a1', caption: 'Beach' }]);
  });

  it('sends visa details only for a visa, and no itinerary with them', () => {
    const built = buildRequest(emptyDraft('Visa'), 'NGN');

    expect(built.ok && built.request.visa?.documents.length).toBe(2);
    expect(built.ok && built.request.itinerary).toEqual([]);
    expect(built.ok && built.request.availableTo).toBeNull();
  });

  it('reads a product back into the editor and saves it unchanged', () => {
    for (const id of ['prd_zanzibar', 'prd_dubai_visa']) {
      const product = stored(id);
      const built = buildRequest(draftFromProduct(product), product.currency);
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      const { id: _id, slug, status, publishedAt, updatedAt, publishProblems, media, ...content } = product;

      expect(built.ok && built.request).toEqual({
        ...content,
        slug: null,
        media: media.map(({ assetId, caption }) => ({ assetId, caption })),
      });
    }
  });

  it('knows a draft is unchanged even though its rows were given new keys', () => {
    const product = stored('prd_zanzibar');

    expect(sameDraft(draftFromProduct(product), draftFromProduct(product))).toBe(true);
    expect(sameDraft(draftFromProduct(product), { ...draftFromProduct(product), title: 'Other' })).toBe(false);
  });
});

describe('small things', () => {
  it('moves an item, and leaves the list alone at the ends', () => {
    expect(moveItem(['a', 'b', 'c'], 0, 1)).toEqual(['b', 'a', 'c']);
    expect(moveItem(['a', 'b', 'c'], 0, -1)).toEqual(['a', 'b', 'c']);
    expect(moveItem(['a', 'b', 'c'], 2, 1)).toEqual(['a', 'b', 'c']);
  });

  it('adds up a visa’s fees, and gives up on one it cannot read', () => {
    const visa = emptyDraft('Visa').visa!;

    expect(visaTotalMinor({ ...visa, consularFee: '145000', serviceFee: '35000' })).toBe(18_000_000);
    expect(visaTotalMinor({ ...visa, consularFee: 'lots' })).toBeNull();
  });

  it('points each publish problem at the part of the editor that fixes it', () => {
    expect(sectionOf({ field: 'media', message: '' })).toBe('images');
    expect(sectionOf({ field: 'visa.documents', message: '' })).toBe('visa');
    expect(sectionOf({ field: 'availableTo', message: '' })).toBe('basics');
  });

  it('reads the product type from the URL', () => {
    expect(typeFromSlug('visa')).toBe('Visa');
    expect(typeFromSlug('boat')).toBeNull();
  });
});
