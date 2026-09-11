import { amountInputFromMinor, parseAmount } from '../pricing/pricing-rules';
import type {
  EntryType,
  InclusionKind,
  Meal,
  PaxType,
  Product,
  ProductFilters,
  ProductMedia,
  ProductRequest,
  ProductStatus,
  ProductSummary,
  ProductType,
  PublishProblem,
} from './types';

/**
 * Everything the catalog screens decide, as plain functions — tested without
 * rendering anything in `__tests__/catalog-rules.test.ts`.
 *
 * What is NOT here: whether a product can be published. That is the server's
 * rule (issue 160), and the screens show the `publishProblems` it sends.
 */

/* ---------------------------------------------------------------- access -- */

// The backend's role map (IdentitySeedData): Owner and Manager hold catalog.edit
// and catalog.publish; an Agent holds catalog.view only. When the session
// carries permissions rather than roles, these become `permissions.includes(…)`.
// The screen only decides what it OFFERS: the API refuses without the permission.
const EDIT_ROLES: ReadonlySet<string> = new Set(['Owner', 'Manager']);
const PUBLISH_ROLES: ReadonlySet<string> = new Set(['Owner', 'Manager']);

export function canEditCatalog(roles: readonly string[]): boolean {
  return roles.some((role) => EDIT_ROLES.has(role));
}

export function canPublishCatalog(roles: readonly string[]): boolean {
  return roles.some((role) => PUBLISH_ROLES.has(role));
}

/* ------------------------------------------------------------------ list -- */

export const PRODUCT_TYPES: ReadonlyArray<{ value: ProductType; label: string; singular: string }> =
  [
    { value: 'Tour', label: 'Tours', singular: 'tour' },
    { value: 'Package', label: 'Packages', singular: 'package' },
    { value: 'Visa', label: 'Visas', singular: 'visa' },
  ];

/** In the URL: `/catalog/new/tour`. */
export function typeFromSlug(slug: string | undefined): ProductType | null {
  return PRODUCT_TYPES.find((type) => type.singular === slug)?.value ?? null;
}

export function singularOf(type: ProductType): string {
  return PRODUCT_TYPES.find((entry) => entry.value === type)?.singular ?? 'product';
}

export const STATUS_FILTERS: ReadonlyArray<{ value: ProductStatus | 'all'; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'Draft', label: 'Drafts' },
  { value: 'Published', label: 'Published' },
  { value: 'Archived', label: 'Archived' },
];

/** Newest change first: the product being worked on is at the top. */
export function filterProducts(
  products: readonly ProductSummary[],
  { type, status, query }: ProductFilters,
): ProductSummary[] {
  const needle = query.trim().toLowerCase();

  return products
    .filter((product) => type === 'all' || product.productType === type)
    .filter((product) => status === 'all' || product.status === status)
    .filter(
      (product) =>
        !needle ||
        [product.title, product.destinationCity, countryName(product.destinationCountry)].some(
          (text) => text.toLowerCase().includes(needle),
        ),
    )
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
}

export function statusCounts(
  products: readonly ProductSummary[],
): Record<ProductStatus | 'all', number> {
  const counts = { all: products.length, Draft: 0, Published: 0, Archived: 0 };
  for (const product of products) counts[product.status] += 1;
  return counts;
}

let regionNames: Intl.DisplayNames | null | undefined;

/** `TZ` → "Tanzania". Falls back to the code where the browser has no names. */
export function countryName(code: string): string {
  if (!code) return '';
  if (regionNames === undefined) {
    try {
      regionNames = new Intl.DisplayNames(['en'], { type: 'region' });
    } catch {
      regionNames = null;
    }
  }
  try {
    return regionNames?.of(code.toUpperCase()) ?? code;
  } catch {
    return code;
  }
}

export function describeDuration(days: number | null): string {
  if (!days) return '';
  return days === 1 ? '1 day' : `${days} days`;
}

/** "Zanzibar, Tanzania · 5 days". */
export function describePlace(product: {
  destinationCity: string;
  destinationCountry: string;
  durationDays: number | null;
}): string {
  const place = [product.destinationCity, countryName(product.destinationCountry)]
    .filter(Boolean)
    .join(', ');
  return [place, describeDuration(product.durationDays)].filter(Boolean).join(' · ');
}

/* ---------------------------------------------------------------- editor -- */

/**
 * The editor's working copy. Amounts and counts stay as the agent typed them
 * ("145,000"), so a half-typed figure is never rewritten under their cursor;
 * `buildRequest` turns them into minor units once, on save.
 *
 * Every list row carries a `key` that survives reordering, for React.
 */
export interface DayDraft {
  key: string;
  title: string;
  description: string;
  meals: Meal[];
  accommodation: string;
}

export interface InclusionDraft {
  key: string;
  kind: InclusionKind;
  text: string;
}

export interface VariantDraft {
  key: string;
  name: string;
  paxType: PaxType;
  occupancy: string;
  minGroupSize: string;
  maxGroupSize: string;
  price: string;
}

export interface DocumentDraft {
  key: string;
  label: string;
  isMandatory: boolean;
}

export interface VisaDraft {
  visaType: string;
  entryType: EntryType;
  processingTimeDays: string;
  validityDays: string;
  consularFee: string;
  serviceFee: string;
  documents: DocumentDraft[];
}

export interface ProductDraft {
  productType: ProductType;
  title: string;
  summary: string;
  description: string;
  destinationCountry: string;
  destinationCity: string;
  durationDays: string;
  basePrice: string;
  availableFrom: string;
  availableTo: string;
  heroAssetId: string | null;
  media: ProductMedia[];
  categoryIds: string[];
  itinerary: DayDraft[];
  inclusions: InclusionDraft[];
  variants: VariantDraft[];
  visa: VisaDraft | null;
}

let keyCounter = 0;

/** A key for a new list row. Unique for the life of the page, which is all React needs. */
export function newKey(): string {
  keyCounter += 1;
  return `row-${keyCounter}`;
}

export const MEALS: readonly Meal[] = ['Breakfast', 'Lunch', 'Dinner'];

export function emptyDay(): DayDraft {
  return { key: newKey(), title: '', description: '', meals: [], accommodation: '' };
}

export function emptyVariant(): VariantDraft {
  return {
    key: newKey(),
    name: '',
    paxType: 'Adult',
    occupancy: '',
    minGroupSize: '',
    maxGroupSize: '',
    price: '',
  };
}

export function emptyVisa(): VisaDraft {
  return {
    visaType: 'Tourist',
    entryType: 'Single',
    processingTimeDays: '',
    validityDays: '',
    consularFee: '',
    serviceFee: '',
    documents: [
      {
        key: newKey(),
        label: 'Passport bio page, valid for at least six months',
        isMandatory: true,
      },
      { key: newKey(), label: 'Passport photograph on a white background', isMandatory: true },
    ],
  };
}

export function emptyDraft(productType: ProductType): ProductDraft {
  return {
    productType,
    title: '',
    summary: '',
    description: '',
    destinationCountry: productType === 'Visa' ? '' : 'NG',
    destinationCity: '',
    durationDays: '',
    basePrice: '',
    availableFrom: '',
    availableTo: '',
    heroAssetId: null,
    media: [],
    categoryIds: [],
    itinerary: productType === 'Visa' ? [] : [emptyDay()],
    inclusions: [],
    variants: [],
    visa: productType === 'Visa' ? emptyVisa() : null,
  };
}

const text = (value: number | null) => (value === null ? '' : String(value));
const money = (amountMinor: number) => (amountMinor === 0 ? '' : amountInputFromMinor(amountMinor));

export function draftFromProduct(product: Product): ProductDraft {
  return {
    productType: product.productType,
    title: product.title,
    summary: product.summary,
    description: product.description,
    destinationCountry: product.destinationCountry,
    destinationCity: product.destinationCity,
    durationDays: text(product.durationDays),
    basePrice: money(product.basePriceMinor),
    availableFrom: product.availableFrom ?? '',
    availableTo: product.availableTo ?? '',
    heroAssetId: product.heroAssetId,
    media: product.media.map((media) => ({ ...media })),
    categoryIds: [...product.categoryIds],
    itinerary: product.itinerary.map((day) => ({
      key: newKey(),
      title: day.title,
      description: day.description,
      meals: [...day.meals],
      accommodation: day.accommodation,
    })),
    inclusions: product.inclusions.map((inclusion) => ({ key: newKey(), ...inclusion })),
    variants: product.priceVariants.map((variant) => ({
      key: newKey(),
      name: variant.name,
      paxType: variant.paxType,
      occupancy: text(variant.occupancy),
      minGroupSize: text(variant.minGroupSize),
      maxGroupSize: text(variant.maxGroupSize),
      price: money(variant.priceMinor),
    })),
    visa: product.visa
      ? {
          visaType: product.visa.visaType,
          entryType: product.visa.entryType,
          processingTimeDays: text(product.visa.processingTimeDays),
          validityDays: text(product.visa.validityDays),
          consularFee: money(product.visa.consularFeeMinor),
          serviceFee: money(product.visa.serviceFeeMinor),
          documents: product.visa.documents.map((document) => ({ key: newKey(), ...document })),
        }
      : null,
  };
}

/** Field key → what is wrong with what was typed there. */
export type FieldErrors = Record<string, string>;

export type BuiltRequest =
  { ok: true; request: ProductRequest } | { ok: false; errors: FieldErrors };

/**
 * The draft as the API takes it.
 *
 * A draft saves in any state — nothing is required here, and a missing price
 * is simply 0 until someone sets it; the publish checklist says what is
 * missing. What is refused is input that is not a number at all, because
 * saving "abc" as a price would lose what the agent meant.
 */
export function buildRequest(draft: ProductDraft, currency: string): BuiltRequest {
  const errors: FieldErrors = {};

  const amount = (field: string, input: string): number => {
    if (!input.trim()) return 0;
    const parsed = parseAmount(input);
    if (parsed.ok) return parsed.value;
    errors[field] = parsed.error;
    return 0;
  };

  const count = (field: string, input: string, { min = 1 } = {}): number | null => {
    const trimmed = input.trim();
    if (!trimmed) return null;
    if (!/^\d+$/.test(trimmed) || Number(trimmed) < min) {
      errors[field] = min === 0 ? 'A whole number.' : `A whole number, ${min} or more.`;
      return null;
    }
    return Number(trimmed);
  };

  const isVisa = draft.productType === 'Visa';

  const request: ProductRequest = {
    productType: draft.productType,
    title: draft.title.trim(),
    slug: null,
    summary: draft.summary.trim(),
    description: draft.description.trim(),
    destinationCountry: draft.destinationCountry.trim().toUpperCase(),
    destinationCity: draft.destinationCity.trim(),
    durationDays: isVisa ? null : count('durationDays', draft.durationDays),
    currency,
    basePriceMinor: amount('basePrice', draft.basePrice),
    availableFrom: isVisa ? null : draft.availableFrom || null,
    availableTo: isVisa ? null : draft.availableTo || null,
    heroAssetId: draft.media.some((media) => media.assetId === draft.heroAssetId)
      ? draft.heroAssetId
      : (draft.media[0]?.assetId ?? null),
    media: draft.media.map(({ assetId, caption }) => ({ assetId, caption: caption.trim() })),
    categoryIds: [...draft.categoryIds],
    itinerary: isVisa
      ? []
      : draft.itinerary.map((day, index) => ({
          dayNumber: index + 1,
          title: day.title.trim(),
          description: day.description.trim(),
          meals: MEALS.filter((meal) => day.meals.includes(meal)),
          accommodation: day.accommodation.trim(),
        })),
    inclusions: isVisa
      ? []
      : draft.inclusions
          .filter((inclusion) => inclusion.text.trim())
          .map(({ kind, text: line }) => ({ kind, text: line.trim() })),
    priceVariants: isVisa
      ? []
      : draft.variants.map((variant, index) => ({
          name: variant.name.trim(),
          paxType: variant.paxType,
          occupancy: count(`variants.${index}.occupancy`, variant.occupancy),
          minGroupSize: count(`variants.${index}.minGroupSize`, variant.minGroupSize),
          maxGroupSize: count(`variants.${index}.maxGroupSize`, variant.maxGroupSize),
          priceMinor: amount(`variants.${index}.price`, variant.price),
        })),
    visa:
      isVisa && draft.visa
        ? {
            visaType: draft.visa.visaType.trim(),
            entryType: draft.visa.entryType,
            processingTimeDays:
              count('visa.processingTimeDays', draft.visa.processingTimeDays, { min: 0 }) ?? 0,
            validityDays: count('visa.validityDays', draft.visa.validityDays) ?? 0,
            consularFeeMinor: amount('visa.consularFee', draft.visa.consularFee),
            serviceFeeMinor: amount('visa.serviceFee', draft.visa.serviceFee),
            documents: draft.visa.documents
              .filter((document) => document.label.trim())
              .map(({ label, isMandatory }) => ({ label: label.trim(), isMandatory })),
          }
        : null,
  };

  request.priceVariants.forEach((variant, index) => {
    if (
      variant.minGroupSize !== null &&
      variant.maxGroupSize !== null &&
      variant.minGroupSize > variant.maxGroupSize
    ) {
      errors[`variants.${index}.maxGroupSize`] = 'Smaller than the minimum.';
    }
  });

  return Object.keys(errors).length > 0 ? { ok: false, errors } : { ok: true, request };
}

/** Whether the working copy differs from what was last saved. Row keys do not count. */
export function sameDraft(a: ProductDraft, b: ProductDraft): boolean {
  return JSON.stringify(a, dropKeys) === JSON.stringify(b, dropKeys);
}

function dropKeys(this: unknown, name: string, value: unknown) {
  return name === 'key' ? undefined : value;
}

/** The list with one item moved up (-1) or down (+1). Out of range: unchanged. */
export function moveItem<T>(list: readonly T[], index: number, direction: -1 | 1): T[] {
  const target = index + direction;
  if (target < 0 || target >= list.length) return [...list];
  const next = [...list];
  [next[index], next[target]] = [next[target] as T, next[index] as T];
  return next;
}

export function removeAt<T>(list: readonly T[], index: number): T[] {
  return list.filter((_, i) => i !== index);
}

export function replaceAt<T>(list: readonly T[], index: number, patch: Partial<T>): T[] {
  return list.map((item, i) => (i === index ? { ...item, ...patch } : item));
}

/** What the customer pays for a visa: the consulate's fee and the agency's. */
export function visaTotalMinor(visa: VisaDraft): number | null {
  const consular = visa.consularFee.trim()
    ? parseAmount(visa.consularFee)
    : { ok: true as const, value: 0 };
  const service = visa.serviceFee.trim()
    ? parseAmount(visa.serviceFee)
    : { ok: true as const, value: 0 };
  return consular.ok && service.ok ? consular.value + service.value : null;
}

/* --------------------------------------------------------------- publish -- */

export type EditorSection =
  'basics' | 'images' | 'itinerary' | 'inclusions' | 'prices' | 'visa' | 'categories';

/** Which part of the editor a publish problem is about, so the checklist can take the agent there. */
export function sectionOf(problem: PublishProblem): EditorSection {
  const field = problem.field;
  if (field === 'media' || field === 'heroAssetId') return 'images';
  if (field.startsWith('visa')) return 'visa';
  if (field.startsWith('itinerary')) return 'itinerary';
  if (field.startsWith('inclusions')) return 'inclusions';
  if (field.startsWith('priceVariants')) return 'prices';
  if (field.startsWith('categor')) return 'categories';
  return 'basics';
}

export const SECTION_LABELS: Record<EditorSection, string> = {
  basics: 'Basics',
  images: 'Images',
  itinerary: 'Itinerary',
  inclusions: 'What’s included',
  prices: 'Prices',
  visa: 'Visa details',
  categories: 'Categories and themes',
};
