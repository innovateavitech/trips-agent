import { ApiError } from '../../../api/errors';
import type { CatalogApi } from '../catalog-api';
import type { Product, ProductRequest, ProductSummary } from '../types';
import { findPublishProblems } from './publish-rules';
import {
  addCategory,
  allCategories,
  allProducts,
  findProduct,
  isoDate,
  putProduct,
  type StoredProduct,
} from './catalog-store';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the product API lands (#161).
 * ============================================================================
 *
 * The catalog screens' stand-in. It keeps the server's rules as #161 states
 * them — slugs unique per agency, every publish problem at once, a published
 * product never saved into an unpublishable state — so the screens are built
 * against the behaviour they will meet, not a friendlier one.
 *
 * Images stay in the browser: an upload becomes an object URL, and the seeded
 * products' images have none, so they show as captioned placeholders.
 */

const LATENCY_MS = 350;
const SAVE_LATENCY_MS = 600;
const UPLOAD_LATENCY_MS = 900;
const MAX_IMAGE_BYTES = 10 * 1024 * 1024;

const delay = (ms = LATENCY_MS) => new Promise((resolve) => setTimeout(resolve, ms));

/** Asset id → the object URL the browser can show, for images uploaded this session. */
const uploaded = new Map<string, string>();

export function slugify(title: string): string {
  return (
    title
      .normalize('NFKD')
      // Accents come apart under NFKD; dropping the marks turns "Café" into "Cafe".
      .replace(/[\u0300-\u036f]/g, '')
      .toLowerCase()
      .replace(/&/g, ' and ')
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '') || 'product'
  );
}

function slugTaken(slug: string, exceptId: string | null): boolean {
  return allProducts().some((product) => product.slug === slug && product.id !== exceptId);
}

/** The slug itself, or the first free `slug-2`, `slug-3`… */
function freeSlug(slug: string, exceptId: string | null): string {
  let candidate = slug;
  for (let n = 2; slugTaken(candidate, exceptId); n += 1) candidate = `${slug}-${n}`;
  return candidate;
}

function withProblems(product: StoredProduct): Product {
  return structuredClone({ ...product, publishProblems: findPublishProblems(product, isoDate(0)) });
}

function toSummary(product: StoredProduct): ProductSummary {
  const hero =
    product.media.find((media) => media.assetId === product.heroAssetId) ?? product.media[0];

  return {
    id: product.id,
    productType: product.productType,
    title: product.title,
    slug: product.slug,
    status: product.status,
    destinationCity: product.destinationCity,
    destinationCountry: product.destinationCountry,
    durationDays: product.durationDays,
    basePriceMinor: product.basePriceMinor,
    currency: product.currency,
    heroPreviewUrl: hero?.previewUrl ?? null,
    updatedAt: product.updatedAt,
    publishProblemCount: findPublishProblems(product, isoDate(0)).length,
  };
}

function mustFind(id: string): StoredProduct {
  const product = findProduct(id);
  if (!product) {
    throw new ApiError(
      404,
      'We could not find that product.',
      'It may have been removed, or belong to another agency.',
    );
  }
  return product;
}

function fromRequest(
  request: ProductRequest,
  existing: StoredProduct | null,
  existingMedia: StoredProduct['media'],
): StoredProduct {
  const id = existing?.id ?? `prd_${crypto.randomUUID().slice(0, 8)}`;
  const wanted = request.slug?.trim() ? slugify(request.slug) : null;

  if (wanted && slugTaken(wanted, id)) {
    throw new ApiError(
      409,
      'That web address is taken.',
      `Another product already uses /${wanted}. /${freeSlug(wanted, id)} is free.`,
    );
  }

  const known = new Map(existingMedia.map((media) => [media.assetId, media.previewUrl]));

  return {
    ...request,
    id,
    slug: wanted ?? existing?.slug ?? freeSlug(slugify(request.title), id),
    status: existing?.status ?? 'Draft',
    publishedAt: existing?.publishedAt ?? null,
    updatedAt: new Date().toISOString(),
    media: request.media.map((media) => ({
      ...media,
      previewUrl: uploaded.get(media.assetId) ?? known.get(media.assetId) ?? null,
    })),
  };
}

export const mockCatalogApi: CatalogApi = {
  async listProducts() {
    await delay();
    return allProducts().map(toSummary);
  },

  async getProduct(id) {
    await delay();
    return withProblems(mustFind(id));
  },

  async createProduct(request) {
    await delay(SAVE_LATENCY_MS);
    const product = fromRequest(request, null, []);
    putProduct(product);
    return withProblems(product);
  },

  async saveProduct(id, request) {
    await delay(SAVE_LATENCY_MS);
    const existing = mustFind(id);

    if (existing.status === 'Archived') {
      throw new ApiError(409, 'Archived products cannot be changed.', 'Restore it as a draft first.');
    }

    const next = fromRequest(request, existing, existing.media);

    if (next.status === 'Published') {
      const problems = findPublishProblems(next, isoDate(0));
      if (problems.length > 0) {
        throw new ApiError(
          422,
          'Saving this would break the published product.',
          `${problems.map((problem) => problem.message).join(' ')} Fix these, or unpublish it first.`,
        );
      }
    }

    putProduct(next);
    return withProblems(next);
  },

  async transition(id, action) {
    await delay(SAVE_LATENCY_MS);
    const product = mustFind(id);
    const now = new Date().toISOString();

    if (action === 'publish') {
      if (product.status !== 'Draft') {
        throw new ApiError(409, 'Only a draft can be published.', 'Restore it as a draft first.');
      }
      const problems = findPublishProblems(product, isoDate(0));
      if (problems.length > 0) {
        throw new ApiError(
          422,
          'This product is not ready to publish.',
          problems.map((problem) => problem.message).join(' '),
        );
      }
      putProduct({ ...product, status: 'Published', publishedAt: now, updatedAt: now });
    } else if (action === 'unpublish') {
      if (product.status === 'Draft') {
        throw new ApiError(409, 'This product is already a draft.');
      }
      putProduct({ ...product, status: 'Draft', updatedAt: now });
    } else {
      if (product.status === 'Archived') {
        throw new ApiError(409, 'This product is already archived.');
      }
      putProduct({ ...product, status: 'Archived', updatedAt: now });
    }

    return withProblems(mustFind(id));
  },

  async listCategories() {
    await delay();
    return structuredClone(allCategories());
  },

  async createCategory({ name, type }) {
    await delay();
    const trimmed = name.trim();

    if (!trimmed) throw new ApiError(422, 'Give it a name.');
    if (
      allCategories().some(
        (category) => category.type === type && category.name.toLowerCase() === trimmed.toLowerCase(),
      )
    ) {
      throw new ApiError(409, `There is already a ${type.toLowerCase()} called ${trimmed}.`);
    }

    const category = { id: `cat_${crypto.randomUUID().slice(0, 8)}`, name: trimmed, type };
    addCategory(category);
    return { ...category };
  },

  async uploadImage(file) {
    await delay(UPLOAD_LATENCY_MS);

    // The server sniffs the content; the stand-in can only trust the browser's word.
    if (!file.type.startsWith('image/')) {
      throw new ApiError(422, 'That file is not an image.', 'Choose a JPEG, PNG or WebP photo.');
    }
    if (file.size > MAX_IMAGE_BYTES) {
      throw new ApiError(422, 'That image is too large.', 'Images can be up to 10 MB.');
    }

    const assetId = `ast_${crypto.randomUUID().slice(0, 8)}`;
    const previewUrl = URL.createObjectURL(file);
    uploaded.set(assetId, previewUrl);
    return { assetId, previewUrl };
  },
};
