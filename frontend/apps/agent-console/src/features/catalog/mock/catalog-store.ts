import type { Category, Product } from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the product API lands (#161).
 * ============================================================================
 *
 * The stand-in's products, in memory: six products in every state, so each
 * screen has something honest to show. Stored without `publishProblems` — the
 * stand-in works those out on every read, as the server does.
 */

export type StoredProduct = Omit<Product, 'publishProblems'>;

/** `YYYY-MM-DD`, some days from today. Dates stay relative so the seed never goes stale. */
export function isoDate(daysFromToday: number): string {
  return new Date(Date.now() + daysFromToday * 86_400_000).toISOString().slice(0, 10);
}

const iso = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString();

const CATEGORIES: Category[] = [
  { id: 'cat_local', name: 'Local tours', type: 'Category' },
  { id: 'cat_intl', name: 'International packages', type: 'Category' },
  { id: 'cat_visa', name: 'Visas', type: 'Category' },
  { id: 'thm_beach', name: 'Beach', type: 'Theme' },
  { id: 'thm_adventure', name: 'Adventure', type: 'Theme' },
  { id: 'thm_honeymoon', name: 'Honeymoon', type: 'Theme' },
  { id: 'thm_family', name: 'Family', type: 'Theme' },
  { id: 'thm_city', name: 'City break', type: 'Theme' },
];

const NONE = {
  itinerary: [],
  inclusions: [],
  priceVariants: [],
  visa: null,
} satisfies Partial<StoredProduct>;

function seed(): StoredProduct[] {
  return [
    {
      id: 'prd_zanzibar',
      productType: 'Package',
      title: 'Zanzibar Beach Escape',
      slug: 'zanzibar-beach-escape',
      status: 'Published',
      summary: 'Four nights on the white sand of Nungwi, with Stone Town and a spice farm.',
      description:
        'A slow week on the north coast of Zanzibar. Days are yours on the beach, with one morning in the alleys of Stone Town and an afternoon on a spice farm. Built for couples and friends travelling from Lagos or Abuja.',
      destinationCountry: 'TZ',
      destinationCity: 'Zanzibar',
      durationDays: 5,
      currency: 'NGN',
      basePriceMinor: 145_000_000,
      availableFrom: isoDate(-30),
      availableTo: isoDate(180),
      heroAssetId: 'ast_zanzibar_1',
      media: [
        { assetId: 'ast_zanzibar_1', caption: 'Nungwi beach at sunset', previewUrl: null },
        { assetId: 'ast_zanzibar_2', caption: 'Stone Town doors', previewUrl: null },
      ],
      categoryIds: ['cat_intl', 'thm_beach', 'thm_honeymoon'],
      itinerary: [
        {
          dayNumber: 1,
          title: 'Arrive in Zanzibar',
          description: 'Met at Abeid Amani Karume airport and driven an hour north to Nungwi.',
          meals: ['Dinner'],
          accommodation: 'Nungwi Dreams Hotel',
        },
        {
          dayNumber: 2,
          title: 'Stone Town and the spice farm',
          description: 'A guided morning walk through Stone Town, then lunch among the clove trees.',
          meals: ['Breakfast', 'Lunch'],
          accommodation: 'Nungwi Dreams Hotel',
        },
        {
          dayNumber: 3,
          title: 'Beach day',
          description: 'Free on the beach, with an optional dhow cruise at sunset.',
          meals: ['Breakfast'],
          accommodation: 'Nungwi Dreams Hotel',
        },
        {
          dayNumber: 4,
          title: 'Mnemba snorkelling',
          description: 'A boat to the Mnemba atoll reef: turtles, reef fish, and often dolphins.',
          meals: ['Breakfast', 'Lunch'],
          accommodation: 'Nungwi Dreams Hotel',
        },
        {
          dayNumber: 5,
          title: 'Fly home',
          description: 'Breakfast, then the transfer back for the flight home.',
          meals: ['Breakfast'],
          accommodation: '',
        },
      ],
      inclusions: [
        { kind: 'Inclusion', text: 'Four nights at a beachfront hotel' },
        { kind: 'Inclusion', text: 'Airport transfers both ways' },
        { kind: 'Inclusion', text: 'Stone Town walking tour and spice farm visit' },
        { kind: 'Inclusion', text: 'Daily breakfast' },
        { kind: 'Exclusion', text: 'International flights' },
        { kind: 'Exclusion', text: 'Zanzibar visa and the island’s travel insurance fee' },
      ],
      priceVariants: [
        {
          name: 'Double room, per adult',
          paxType: 'Adult',
          occupancy: 2,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 145_000_000,
        },
        {
          name: 'Single room',
          paxType: 'Adult',
          occupancy: 1,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 185_000_000,
        },
        {
          name: 'Child 2–11, sharing with adults',
          paxType: 'Child',
          occupancy: null,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 89_000_000,
        },
        {
          name: 'Infant under 2',
          paxType: 'Infant',
          occupancy: null,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 15_000_000,
        },
      ],
      visa: null,
      publishedAt: iso(20),
      updatedAt: iso(3),
    },
    {
      id: 'prd_obudu',
      productType: 'Tour',
      title: 'Obudu Mountain Resort Weekend',
      slug: 'obudu-mountain-resort-weekend',
      status: 'Published',
      summary: 'The cable car, the canopy walk and cool mountain air, two hours from Calabar.',
      description:
        'A long weekend on the Obudu plateau, 1,600 metres up. Ride the cable car, cross the canopy walkway, swim under the Grotto waterfall, and sleep somewhere you need a blanket.',
      destinationCountry: 'NG',
      destinationCity: 'Obudu',
      durationDays: 3,
      currency: 'NGN',
      basePriceMinor: 38_500_000,
      availableFrom: isoDate(-60),
      availableTo: isoDate(120),
      heroAssetId: 'ast_obudu_1',
      media: [{ assetId: 'ast_obudu_1', caption: 'The Obudu cable car', previewUrl: null }],
      categoryIds: ['cat_local', 'thm_adventure', 'thm_family'],
      itinerary: [
        {
          dayNumber: 1,
          title: 'Calabar to the plateau',
          description: 'Road transfer from Calabar, arriving for sunset over the ranch.',
          meals: ['Dinner'],
          accommodation: 'Obudu Mountain Resort',
        },
        {
          dayNumber: 2,
          title: 'Canopy walk and the Grotto',
          description: 'The canopy walkway in the morning; the waterfall and a picnic after.',
          meals: ['Breakfast', 'Lunch', 'Dinner'],
          accommodation: 'Obudu Mountain Resort',
        },
        {
          dayNumber: 3,
          title: 'Cable car and home',
          description: 'The cable car down the escarpment, then the road back to Calabar.',
          meals: ['Breakfast'],
          accommodation: '',
        },
      ],
      inclusions: [
        { kind: 'Inclusion', text: 'Return road transfer from Calabar' },
        { kind: 'Inclusion', text: 'Two nights in a chalet' },
        { kind: 'Inclusion', text: 'Cable car and canopy walk tickets' },
        { kind: 'Exclusion', text: 'Flights to Calabar' },
      ],
      priceVariants: [
        {
          name: 'Adult, chalet shared by two',
          paxType: 'Adult',
          occupancy: 2,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 38_500_000,
        },
        {
          name: 'Child 2–11',
          paxType: 'Child',
          occupancy: null,
          minGroupSize: null,
          maxGroupSize: null,
          priceMinor: 21_000_000,
        },
      ],
      visa: null,
      publishedAt: iso(45),
      updatedAt: iso(12),
    },
    {
      id: 'prd_lagos_heritage',
      productType: 'Tour',
      title: 'Lagos Heritage and Lekki Conservation Day Tour',
      slug: 'lagos-heritage-and-lekki-conservation-day-tour',
      status: 'Draft',
      summary: 'Freedom Park, the Nike Art Gallery and the canopy walkway, in one day.',
      description: '',
      destinationCountry: 'NG',
      destinationCity: 'Lagos',
      durationDays: 1,
      currency: 'NGN',
      basePriceMinor: 4_500_000,
      availableFrom: null,
      availableTo: null,
      heroAssetId: null,
      media: [],
      categoryIds: ['cat_local', 'thm_city'],
      itinerary: [
        {
          dayNumber: 1,
          title: 'Lagos in a day',
          description: 'Freedom Park, lunch in Lekki, the Nike Art Gallery, then the canopy walkway.',
          meals: ['Lunch'],
          accommodation: '',
        },
      ],
      inclusions: [{ kind: 'Inclusion', text: 'Air-conditioned transport and a guide' }],
      priceVariants: [],
      visa: null,
      publishedAt: null,
      updatedAt: iso(1),
    },
    {
      id: 'prd_dubai_visa',
      productType: 'Visa',
      title: 'Dubai 30-day Tourist Visa',
      slug: 'dubai-30-day-tourist-visa',
      status: 'Published',
      summary: 'Single entry, 30 days in the UAE. We handle the application from start to finish.',
      description:
        'Send us the documents below and we prepare, submit and follow up the application. Most are decided within five working days.',
      destinationCountry: 'AE',
      destinationCity: 'Dubai',
      durationDays: null,
      currency: 'NGN',
      basePriceMinor: 18_000_000,
      availableFrom: null,
      availableTo: null,
      heroAssetId: 'ast_dubai_1',
      media: [{ assetId: 'ast_dubai_1', caption: 'Dubai Marina', previewUrl: null }],
      categoryIds: ['cat_visa'],
      ...NONE,
      visa: {
        visaType: 'Tourist',
        entryType: 'Single',
        processingTimeDays: 5,
        validityDays: 60,
        consularFeeMinor: 14_500_000,
        serviceFeeMinor: 3_500_000,
        documents: [
          { label: 'Passport bio page, valid for at least six months', isMandatory: true },
          { label: 'Passport photograph on a white background', isMandatory: true },
          { label: 'Bank statement for the last three months', isMandatory: true },
          { label: 'Confirmed return ticket', isMandatory: false },
          { label: 'Hotel booking', isMandatory: false },
        ],
      },
      publishedAt: iso(60),
      updatedAt: iso(9),
    },
    {
      id: 'prd_uk_visa',
      productType: 'Visa',
      title: 'UK Standard Visitor Visa',
      slug: 'uk-standard-visitor-visa',
      status: 'Draft',
      summary: 'Up to six months in the UK for tourism, family visits or short business trips.',
      description: '',
      destinationCountry: 'GB',
      destinationCity: 'London',
      durationDays: null,
      currency: 'NGN',
      basePriceMinor: 32_000_000,
      availableFrom: null,
      availableTo: null,
      heroAssetId: null,
      media: [],
      categoryIds: ['cat_visa'],
      ...NONE,
      visa: {
        visaType: 'Standard Visitor',
        entryType: 'Multiple',
        processingTimeDays: 21,
        validityDays: 180,
        consularFeeMinor: 25_000_000,
        serviceFeeMinor: 7_000_000,
        documents: [],
      },
      publishedAt: null,
      updatedAt: iso(2),
    },
    {
      id: 'prd_cape_town',
      productType: 'Package',
      title: 'Cape Town and the Garden Route',
      slug: 'cape-town-and-the-garden-route',
      status: 'Archived',
      summary: 'Table Mountain, the winelands and the coast road to Knysna.',
      description: 'Retired after the 2025 season.',
      destinationCountry: 'ZA',
      destinationCity: 'Cape Town',
      durationDays: 8,
      currency: 'NGN',
      basePriceMinor: 265_000_000,
      availableFrom: isoDate(-400),
      availableTo: isoDate(-40),
      heroAssetId: 'ast_cape_1',
      media: [{ assetId: 'ast_cape_1', caption: 'Table Mountain from the V&A', previewUrl: null }],
      categoryIds: ['cat_intl', 'thm_city'],
      ...NONE,
      publishedAt: iso(400),
      updatedAt: iso(40),
    },
  ];
}

let products: StoredProduct[] | null = null;
let categories: Category[] | null = null;

export function allProducts(): StoredProduct[] {
  return (products ??= seed());
}

export function findProduct(id: string): StoredProduct | undefined {
  return allProducts().find((product) => product.id === id);
}

export function putProduct(product: StoredProduct): void {
  const list = allProducts();
  const index = list.findIndex((existing) => existing.id === product.id);
  if (index < 0) list.unshift(product);
  else list[index] = product;
}

export function allCategories(): Category[] {
  return (categories ??= CATEGORIES.map((category) => ({ ...category })));
}

export function addCategory(category: Category): void {
  allCategories().push(category);
}

/** For tests: start again from the seed. */
export function resetCatalogStore(): void {
  products = null;
  categories = null;
}
