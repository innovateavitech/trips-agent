import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { buildRequest, draftFromProduct, emptyDraft } from '../catalog-rules';
import { SEED, isoDate, resetCatalogStore } from '../mock/catalog-store';
import { mockCatalogApi as api } from '../mock/mock-catalog-api';
import { findPublishProblems } from '../mock/publish-rules';
import type { Product, ProductRequest, ProductType } from '../types';

/**
 * The stand-in keeps the server's rules as issue 161 states them, so the screens are
 * built against the behaviour they will meet. These tests hold it to them.
 */

beforeEach(() => {
  resetCatalogStore();
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

async function settle<T>(promise: Promise<T>): Promise<T> {
  await vi.runAllTimersAsync();
  return promise;
}

async function refused(promise: Promise<unknown>, status: number): Promise<void> {
  const assertion = expect(promise).rejects.toMatchObject({ status });
  await vi.runAllTimersAsync();
  await assertion;
}

function requestFrom(product: Product): ProductRequest {
  const built = buildRequest(draftFromProduct(product), product.currency);
  if (!built.ok) throw new Error('unexpected field errors');
  return built.request;
}

function emptyRequest(type: ProductType): ProductRequest {
  const built = buildRequest(emptyDraft(type), 'NGN');
  if (!built.ok) throw new Error('unexpected field errors');
  return built.request;
}

/** A stored product made from a request; the patch wins over everything. */
function stored(
  request: ProductRequest,
  patch: Partial<Omit<Product, 'publishProblems'>> = {},
): Omit<Product, 'publishProblems'> {
  return {
    ...request,
    id: 'x',
    slug: 'x',
    status: 'Draft',
    publishedAt: null,
    updatedAt: isoDate(0),
    media: [],
    ...patch,
  };
}

describe('publish rules', () => {
  const today = isoDate(0);
  const image = [{ assetId: 'a', caption: '', previewUrl: null }];

  it('lists every problem at once, not just the first', () => {
    expect(
      findPublishProblems(stored(emptyRequest('Tour')), today).map((problem) => problem.field),
    ).toEqual(['title', 'basePriceMinor', 'media', 'availableTo']);
  });

  it('refuses a booking window that has ended', () => {
    const ended = stored(emptyRequest('Tour'), {
      title: 'T',
      basePriceMinor: 1,
      media: image,
      availableTo: isoDate(-1),
    });

    expect(findPublishProblems(ended, today)).toEqual([
      { field: 'availableTo', message: 'The booking window has ended. Move the end date.' },
    ]);
  });

  it('wants a visa to list at least one required document', () => {
    const request = emptyRequest('Visa');
    const product = stored(request, {
      title: 'Visa',
      basePriceMinor: 1,
      media: image,
      visa: { ...request.visa!, documents: [{ label: 'Hotel booking', isMandatory: false }] },
    });

    expect(findPublishProblems(product, today).map((problem) => problem.field)).toEqual([
      'visa.documents',
    ]);
  });
});

describe('the stand-in API', () => {
  it('refuses to publish a draft with problems, and names them', async () => {
    await refused(api.transition(SEED.lagosHeritage, 'publish'), 422);

    const product = await settle(api.getProduct(SEED.lagosHeritage));
    expect(product.status).toBe('Draft');
    expect(product.publishProblems.map((problem) => problem.field)).toEqual([
      'media',
      'availableTo',
    ]);
  });

  it('makes a unique web address from the title', async () => {
    const draft = { ...emptyDraft('Package'), title: 'Zanzibar Beach Escape' };
    const built = buildRequest(draft, 'NGN');
    if (!built.ok) throw new Error('unexpected field errors');

    const created = await settle(api.createProduct(built.request));

    expect(created.slug).toBe('zanzibar-beach-escape-2');
    expect(created.status).toBe('Draft');
  });

  it('refuses a web address that is taken, and suggests a free one', async () => {
    const product = await settle(api.getProduct(SEED.obudu));

    await refused(
      api.saveProduct(SEED.obudu, { ...requestFrom(product), slug: 'zanzibar-beach-escape' }),
      409,
    );
  });

  it('never saves a published product into a state it could not be published in', async () => {
    const product = await settle(api.getProduct(SEED.obudu));

    await refused(
      api.saveProduct(SEED.obudu, { ...requestFrom(product), media: [], heroAssetId: null }),
      422,
    );
  });

  it('refuses to change an archived product, and restores it as a draft', async () => {
    const product = await settle(api.getProduct(SEED.capeTown));
    await refused(api.saveProduct(SEED.capeTown, requestFrom(product)), 409);

    const restored = await settle(api.transition(SEED.capeTown, 'unpublish'));
    expect(restored.status).toBe('Draft');
  });

  it('publishes a draft once nothing is missing', async () => {
    const product = await settle(api.getProduct(SEED.lagosHeritage));
    await settle(
      api.saveProduct(SEED.lagosHeritage, {
        ...requestFrom(product),
        media: [{ assetId: 'ast_x', caption: 'Freedom Park' }],
        availableTo: isoDate(30),
      }),
    );

    const published = await settle(api.transition(SEED.lagosHeritage, 'publish'));

    expect(published.status).toBe('Published');
    expect(published.publishedAt).not.toBeNull();
  });
});
