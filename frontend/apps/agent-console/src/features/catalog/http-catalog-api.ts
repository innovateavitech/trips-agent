import type { Schemas } from '@trips/api-client';
import { api } from '../../api/client';
import { API_BASE_URL } from '../../api/config';
import { ApiError, unwrap } from '../../api/errors';
import { toOptionalWholeNumber, toWholeNumber } from '../pricing/pricing-rules';
import type { CatalogApi } from './catalog-api';
import type {
  Category,
  CategoryType,
  EntryType,
  InclusionKind,
  Meal,
  PaxType,
  Product,
  ProductStatus,
  ProductSummary,
  ProductType,
  UploadedImage,
} from './types';

/**
 * The catalog port over the real API (build plan F3, issues 160 and 161). The
 * screens never know which adapter they have: `main.tsx` chooses.
 *
 * The API sends 64-bit numbers as `number | string` and enums as plain
 * strings; everything is turned into the console's types here, once.
 */

export function toProduct(raw: Schemas['ProductResponse']): Product {
  return {
    id: raw.id,
    productType: raw.productType as ProductType,
    title: raw.title,
    slug: raw.slug,
    status: raw.status as ProductStatus,
    summary: raw.summary,
    description: raw.description,
    destinationCountry: raw.destinationCountry,
    destinationCity: raw.destinationCity,
    durationDays: toOptionalWholeNumber(raw.durationDays),
    currency: raw.currency,
    basePriceMinor: toWholeNumber(raw.basePriceMinor),
    availableFrom: raw.availableFrom,
    availableTo: raw.availableTo,
    heroAssetId: raw.heroAssetId,
    media: raw.media.map((media) => ({
      assetId: media.assetId,
      caption: media.caption,
      previewUrl: media.previewUrl,
    })),
    categoryIds: [...raw.categoryIds],
    itinerary: raw.itinerary.map((day) => ({
      dayNumber: toWholeNumber(day.dayNumber),
      title: day.title,
      description: day.description,
      meals: day.meals as Meal[],
      accommodation: day.accommodation,
    })),
    inclusions: raw.inclusions.map((inclusion) => ({
      kind: inclusion.kind as InclusionKind,
      text: inclusion.text,
    })),
    priceVariants: raw.priceVariants.map((variant) => ({
      name: variant.name,
      paxType: variant.paxType as PaxType,
      occupancy: toOptionalWholeNumber(variant.occupancy),
      minGroupSize: toOptionalWholeNumber(variant.minGroupSize),
      maxGroupSize: toOptionalWholeNumber(variant.maxGroupSize),
      priceMinor: toWholeNumber(variant.priceMinor),
    })),
    visa: raw.visa
      ? {
          visaType: raw.visa.visaType,
          entryType: raw.visa.entryType as EntryType,
          processingTimeDays: toWholeNumber(raw.visa.processingTimeDays),
          validityDays: toWholeNumber(raw.visa.validityDays),
          consularFeeMinor: toWholeNumber(raw.visa.consularFeeMinor),
          serviceFeeMinor: toWholeNumber(raw.visa.serviceFeeMinor),
          documents: raw.visa.documents.map((document) => ({
            label: document.label,
            isMandatory: document.isMandatory,
          })),
        }
      : null,
    publishedAt: raw.publishedAt,
    updatedAt: raw.updatedAt,
    publishProblems: raw.publishProblems.map((problem) => ({
      field: problem.field,
      message: problem.message,
    })),
  };
}

export function toSummary(raw: Schemas['ProductSummaryResponse']): ProductSummary {
  return {
    id: raw.id,
    productType: raw.productType as ProductType,
    title: raw.title,
    slug: raw.slug,
    status: raw.status as ProductStatus,
    destinationCity: raw.destinationCity,
    destinationCountry: raw.destinationCountry,
    durationDays: toOptionalWholeNumber(raw.durationDays),
    basePriceMinor: toWholeNumber(raw.basePriceMinor),
    currency: raw.currency,
    heroPreviewUrl: raw.heroPreviewUrl,
    updatedAt: raw.updatedAt,
    publishProblemCount: toWholeNumber(raw.publishProblemCount),
  };
}

function toCategory(raw: Schemas['CategoryResponse']): Category {
  return { id: raw.id, name: raw.name, type: raw.type as CategoryType };
}

/** The picture a screen should show: a medium rendition, else the nearest one there is. */
export function previewLink(
  links: readonly Pick<Schemas['AssetLinkResponse'], 'kind' | 'url'>[],
): string | null {
  for (const kind of ['Medium', 'Large', 'Thumbnail', 'Original']) {
    const link = links.find((candidate) => candidate.kind === kind);
    if (link) return link.url;
  }
  return null;
}

/** For calls that answer with no body: a success is enough. */
async function ensureOk(pending: Promise<{ error?: unknown; response: Response }>): Promise<void> {
  const result = await pending;
  if (result.error !== undefined || !result.response.ok)
    throw ApiError.from(result.response, result.error);
}

const SCAN_POLL_MS = 1_500;
const SCAN_POLL_ATTEMPTS = 40; // about a minute

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * An image goes through the asset pipeline like every upload: ask for a signed
 * URL, send the file straight to storage, say it is done, then wait for the
 * scan. Nothing can be attached until it is Ready.
 */
async function uploadImage(file: File): Promise<UploadedImage> {
  const upload = await unwrap(
    api.POST('/api/v1/assets/uploads', {
      body: {
        purpose: 'ProductMedia',
        fileName: file.name,
        sizeBytes: file.size,
        contentType: file.type || null,
      },
    }),
  );

  const target = new URL(upload.uploadUrl, API_BASE_URL || window.location.origin);
  const sent = await fetch(target, { method: upload.method, headers: upload.headers, body: file });
  if (!sent.ok) {
    throw new ApiError(
      sent.status,
      'The image did not upload.',
      'Try again, or choose a smaller image.',
    );
  }

  const byId = { params: { path: { assetId: upload.assetId } } };
  await ensureOk(api.POST('/api/v1/assets/{assetId}/complete', byId));

  for (let attempt = 0; attempt < SCAN_POLL_ATTEMPTS; attempt += 1) {
    const asset = await unwrap(api.GET('/api/v1/assets/{assetId}', byId));

    if (asset.status === 'Ready')
      return { assetId: asset.id, previewUrl: previewLink(asset.links) ?? '' };
    if (asset.status === 'Quarantined') {
      throw new ApiError(
        422,
        'That file did not pass the virus scan.',
        'Choose a different image.',
      );
    }
    if (asset.status === 'Failed') {
      throw new ApiError(
        422,
        'We could not use that image.',
        asset.failureReason ?? 'Choose a JPEG, PNG or WebP photo.',
      );
    }
    await wait(SCAN_POLL_MS);
  }

  throw new ApiError(
    504,
    'The image is still being checked.',
    'It can take a minute. Try adding it again shortly.',
  );
}

const byProduct = (productId: string) => ({ params: { path: { productId } } });

export const httpCatalogApi: CatalogApi = {
  async listProducts() {
    return (await unwrap(api.GET('/api/v1/catalog/products'))).map(toSummary);
  },

  async getProduct(id) {
    return toProduct(await unwrap(api.GET('/api/v1/catalog/products/{productId}', byProduct(id))));
  },

  async createProduct(request) {
    return toProduct(await unwrap(api.POST('/api/v1/catalog/products', { body: request })));
  },

  async saveProduct(id, request) {
    return toProduct(
      await unwrap(
        api.PUT('/api/v1/catalog/products/{productId}', { ...byProduct(id), body: request }),
      ),
    );
  },

  async transition(id, action) {
    const raw =
      action === 'publish'
        ? await unwrap(api.POST('/api/v1/catalog/products/{productId}/publish', byProduct(id)))
        : action === 'unpublish'
          ? await unwrap(api.POST('/api/v1/catalog/products/{productId}/unpublish', byProduct(id)))
          : await unwrap(api.POST('/api/v1/catalog/products/{productId}/archive', byProduct(id)));
    return toProduct(raw);
  },

  async listCategories() {
    return (await unwrap(api.GET('/api/v1/catalog/categories'))).map(toCategory);
  },

  async createCategory({ name, type }) {
    return toCategory(
      await unwrap(api.POST('/api/v1/catalog/categories', { body: { name, type } })),
    );
  },

  uploadImage,
};
