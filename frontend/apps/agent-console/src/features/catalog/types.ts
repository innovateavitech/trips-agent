/**
 * The catalog as the console sees it: the products an agency authors and sells
 * under its own brand — tours, packages and visas (#56).
 *
 * Field for field the contract in #161, so the stand-in in `mock/` and the HTTP
 * adapter that replaces it return the same shapes.
 */

export type ProductType = 'Tour' | 'Package' | 'Visa';
export type ProductStatus = 'Draft' | 'Published' | 'Archived';
export type Meal = 'Breakfast' | 'Lunch' | 'Dinner';
export type InclusionKind = 'Inclusion' | 'Exclusion';
export type PaxType = 'Adult' | 'Child' | 'Infant';
export type EntryType = 'Single' | 'Multiple';
export type CategoryType = 'Category' | 'Theme';

export interface ItineraryDay {
  dayNumber: number;
  title: string;
  description: string;
  meals: Meal[];
  accommodation: string;
}

export interface Inclusion {
  kind: InclusionKind;
  text: string;
}

/** A price for one kind of traveller: "Double occupancy, adult". */
export interface PriceVariant {
  name: string;
  paxType: PaxType;
  /** People sharing a room, where the price depends on it. */
  occupancy: number | null;
  minGroupSize: number | null;
  maxGroupSize: number | null;
  priceMinor: number;
}

export interface VisaDocument {
  label: string;
  isMandatory: boolean;
}

export interface VisaDetails {
  /** "Tourist", "Business", "Transit": the agent's words, shown to the customer. */
  visaType: string;
  entryType: EntryType;
  processingTimeDays: number;
  validityDays: number;
  consularFeeMinor: number;
  serviceFeeMinor: number;
  documents: VisaDocument[];
}

export interface MediaReference {
  assetId: string;
  caption: string;
}

/** An image as the console shows it. */
export interface ProductMedia extends MediaReference {
  /** A short-lived link to the image; null until the scan has cleared it. */
  previewUrl: string | null;
}

export interface Category {
  id: string;
  name: string;
  type: CategoryType;
}

/** One reason a product cannot be published yet, from the server's rules. */
export interface PublishProblem {
  /** The field it is about — `title`, `media`, `visa.documents` — so the editor can point at it. */
  field: string;
  message: string;
}

/** What the console sends to create or save a product: the whole product, every time. */
export interface ProductRequest {
  productType: ProductType;
  title: string;
  /** Omitted: made from the title, unique within the agency. */
  slug: string | null;
  summary: string;
  description: string;
  /** ISO 3166 alpha-2: `NG`, `AE`. */
  destinationCountry: string;
  destinationCity: string;
  durationDays: number | null;
  currency: string;
  /** The "from" price the storefront shows, in minor units. */
  basePriceMinor: number;
  /** `YYYY-MM-DD`: the window in which it can be bought. Dated departures are #57. */
  availableFrom: string | null;
  availableTo: string | null;
  heroAssetId: string | null;
  media: MediaReference[];
  categoryIds: string[];
  itinerary: ItineraryDay[];
  inclusions: Inclusion[];
  priceVariants: PriceVariant[];
  visa: VisaDetails | null;
}

export interface Product extends Omit<ProductRequest, 'slug' | 'media'> {
  id: string;
  slug: string;
  status: ProductStatus;
  publishedAt: string | null;
  updatedAt: string;
  media: ProductMedia[];
  publishProblems: PublishProblem[];
}

/** A row in the products list. */
export interface ProductSummary {
  id: string;
  productType: ProductType;
  title: string;
  slug: string;
  status: ProductStatus;
  destinationCity: string;
  destinationCountry: string;
  durationDays: number | null;
  basePriceMinor: number;
  currency: string;
  heroPreviewUrl: string | null;
  updatedAt: string;
  publishProblemCount: number;
}

export interface ProductFilters {
  type: ProductType | 'all';
  status: ProductStatus | 'all';
  query: string;
}

export const NO_PRODUCT_FILTERS: ProductFilters = { type: 'all', status: 'all', query: '' };

/** `unpublish` also restores an archived product, as a draft. */
export type ProductTransition = 'publish' | 'unpublish' | 'archive';

export interface UploadedImage {
  assetId: string;
  previewUrl: string;
}
