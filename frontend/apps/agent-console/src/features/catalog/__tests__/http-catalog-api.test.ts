import { describe, expect, it } from 'vitest';
import { previewLink, toProduct, toSummary } from '../http-catalog-api';

/** The API sends 64-bit numbers as strings when they will not fit a JSON number; the adapter turns them into numbers once. */

describe('reading the catalog API', () => {
  it('turns a product response into the console’s product, numbers and all', () => {
    const product = toProduct({
      id: 'p1',
      productType: 'Visa',
      title: 'Dubai 30-day Tourist Visa',
      slug: 'dubai-30-day-tourist-visa',
      summary: '',
      description: '',
      destinationCountry: 'AE',
      destinationCity: 'Dubai',
      durationDays: null,
      currency: 'NGN',
      basePriceMinor: '18000000',
      availableFrom: null,
      availableTo: null,
      heroAssetId: 'a1',
      media: [{ assetId: 'a1', caption: 'Dubai Marina', previewUrl: null }],
      categoryIds: ['c1'],
      itinerary: [],
      inclusions: [],
      priceVariants: [],
      visa: {
        visaType: 'Tourist',
        entryType: 'Single',
        processingTimeDays: 5,
        validityDays: '60',
        consularFeeMinor: '14500000',
        serviceFeeMinor: 3_500_000,
        documents: [{ label: 'Passport bio page', isMandatory: true }],
      },
      status: 'Published',
      publishedAt: '2026-09-01T10:00:00Z',
      updatedAt: '2026-09-02T10:00:00Z',
      publishProblems: [],
    });

    expect(product.basePriceMinor).toBe(18_000_000);
    expect(product.visa).toMatchObject({
      validityDays: 60,
      consularFeeMinor: 14_500_000,
      serviceFeeMinor: 3_500_000,
    });
    expect(product.status).toBe('Published');
  });

  it('turns a list row into the console’s summary', () => {
    expect(
      toSummary({
        id: 'p1',
        productType: 'Tour',
        title: 'Obudu weekend',
        slug: 'obudu-weekend',
        status: 'Draft',
        destinationCity: 'Obudu',
        destinationCountry: 'NG',
        durationDays: '3',
        basePriceMinor: '38500000',
        currency: 'NGN',
        heroPreviewUrl: null,
        updatedAt: '2026-09-02T10:00:00Z',
        publishProblemCount: 2,
      }),
    ).toMatchObject({ durationDays: 3, basePriceMinor: 38_500_000, publishProblemCount: 2 });
  });

  it('shows the medium rendition of an image, and the nearest one when there is none', () => {
    expect(
      previewLink([
        { kind: 'Original', url: 'o' },
        { kind: 'Medium', url: 'm' },
      ]),
    ).toBe('m');
    expect(previewLink([{ kind: 'Original', url: 'o' }])).toBe('o');
    expect(previewLink([])).toBeNull();
  });
});
