import { SEED, findProduct, isoDate } from '../../catalog/mock/catalog-store';
import type { DepartureRequest, DepartureStatus, ManifestEntry, WaitlistEntry } from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the departures API lands.
 * ============================================================================
 *
 * The stand-in's departures, in memory: five of them across two products, one
 * in each state a departure can be in — open, guaranteed, nearly full, sold out
 * with a waitlist, and closed by the agent.
 */

export interface StoredDeparture extends DepartureRequest {
  id: string;
  productId: string;
  capacityReserved: number;
  capacityConfirmed: number;
  /** The agent's own decision; everything else about the status comes from the seats. */
  manualState: 'Closed' | 'Cancelled' | null;
  version: number;
  manifest: ManifestEntry[];
  waitlist: WaitlistEntry[];
}

const NAMES = [
  'Adaeze Okafor',
  'Tunde Bakare',
  'Ngozi Eze',
  'Emeka Nwosu',
  'Folake Adeyemi',
  'Chinedu Obi',
  'Aisha Bello',
  'Kunle Ajayi',
  'Zainab Musa',
  'Ifeoma Nnaji',
  'Segun Oladipo',
  'Halima Yusuf',
  'Obinna Okeke',
  'Titilayo Ade',
  'Musa Danjuma',
  'Chioma Uche',
  'Babatunde Lawal',
  'Amaka Obi',
];

/** One traveller per seat, confirmed first, two to a room. */
function manifestFor(prefix: string, confirmed: number, reserved: number): ManifestEntry[] {
  return Array.from({ length: confirmed + reserved }, (_, seat) => ({
    orderReference: `TRP-${prefix}${String(Math.floor(seat / 2) + 1).padStart(2, '0')}`,
    travellerName: NAMES[seat % NAMES.length] ?? 'Traveller',
    paxType: seat % 7 === 6 ? ('Child' as const) : ('Adult' as const),
    room: seat < confirmed ? `Room ${Math.floor(seat / 2) + 1}` : null,
    status: seat < confirmed ? ('Confirmed' as const) : ('Reserved' as const),
  }));
}

const ZANZIBAR_TERMS = {
  isGroupDeparture: true,
  cutoffDaysBefore: 21,
  depositType: 'Percent' as const,
  depositPercentBasisPoints: 3000,
  depositAmountMinor: null,
  priceTiers: [
    { minPax: 1, maxPax: 3, pricePerPaxMinor: 145_000_000 },
    { minPax: 4, maxPax: 7, pricePerPaxMinor: 139_000_000 },
    { minPax: 8, maxPax: null, pricePerPaxMinor: 132_000_000 },
  ],
  installments: [
    {
      sequence: 1,
      dueBasis: 'BeforeDeparture' as const,
      dueOffsetDays: 60,
      percentOfBalanceBasisPoints: 5000,
    },
    {
      sequence: 2,
      dueBasis: 'BeforeDeparture' as const,
      dueOffsetDays: 30,
      percentOfBalanceBasisPoints: 5000,
    },
  ],
};

const OBUDU_TERMS = {
  isGroupDeparture: true,
  cutoffDaysBefore: 7,
  depositType: 'Fixed' as const,
  depositPercentBasisPoints: null,
  depositAmountMinor: 5_000_000,
  priceTiers: [{ minPax: 1, maxPax: null, pricePerPaxMinor: 38_500_000 }],
  installments: [],
};

function seed(): StoredDeparture[] {
  const joined = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString();

  return [
    {
      id: '7a1e5c90-4b2d-4f8e-8c3a-2d9b6e1f0a01',
      productId: SEED.zanzibar,
      departureDate: isoDate(45),
      minPax: 6,
      capacityTotal: 16,
      ...ZANZIBAR_TERMS,
      capacityConfirmed: 9,
      capacityReserved: 2,
      manualState: null,
      version: 3,
      manifest: manifestFor('ZNZ', 9, 2),
      waitlist: [],
    },
    {
      id: '7a1e5c90-4b2d-4f8e-8c3a-2d9b6e1f0a02',
      productId: SEED.zanzibar,
      departureDate: isoDate(75),
      minPax: 6,
      capacityTotal: 16,
      ...ZANZIBAR_TERMS,
      capacityConfirmed: 2,
      capacityReserved: 1,
      manualState: null,
      version: 1,
      manifest: manifestFor('ZNY', 2, 1),
      waitlist: [],
    },
    {
      id: '7a1e5c90-4b2d-4f8e-8c3a-2d9b6e1f0a03',
      productId: SEED.zanzibar,
      departureDate: isoDate(24),
      minPax: 6,
      capacityTotal: 12,
      ...ZANZIBAR_TERMS,
      capacityConfirmed: 11,
      capacityReserved: 1,
      manualState: null,
      version: 7,
      manifest: manifestFor('ZNX', 11, 1),
      waitlist: [
        {
          id: 'w1',
          name: 'Kemi Alabi',
          paxCount: 2,
          status: 'Offered',
          joinedAt: joined(9),
          offeredAt: joined(0),
          expiresAt: new Date(Date.now() + 20 * 3_600_000).toISOString(),
        },
        {
          id: 'w2',
          name: 'Yusuf Garba',
          paxCount: 1,
          status: 'Waiting',
          joinedAt: joined(6),
          offeredAt: null,
          expiresAt: null,
        },
        {
          id: 'w3',
          name: 'Ebere Kalu',
          paxCount: 4,
          status: 'Waiting',
          joinedAt: joined(2),
          offeredAt: null,
          expiresAt: null,
        },
      ],
    },
    {
      id: '7a1e5c90-4b2d-4f8e-8c3a-2d9b6e1f0a04',
      productId: SEED.obudu,
      departureDate: isoDate(14),
      minPax: 8,
      capacityTotal: 20,
      ...OBUDU_TERMS,
      capacityConfirmed: 17,
      capacityReserved: 1,
      manualState: null,
      version: 4,
      manifest: manifestFor('OBU', 17, 1),
      waitlist: [],
    },
    {
      id: '7a1e5c90-4b2d-4f8e-8c3a-2d9b6e1f0a05',
      productId: SEED.obudu,
      departureDate: isoDate(40),
      minPax: 8,
      capacityTotal: 20,
      ...OBUDU_TERMS,
      capacityConfirmed: 0,
      capacityReserved: 0,
      manualState: 'Closed',
      version: 2,
      manifest: [],
      waitlist: [],
    },
  ];
}

let departures: StoredDeparture[] | null = null;

export function allDepartures(): StoredDeparture[] {
  return (departures ??= seed());
}

export function findDeparture(id: string): StoredDeparture | undefined {
  return allDepartures().find((departure) => departure.id === id);
}

export function putDeparture(departure: StoredDeparture): void {
  const list = allDepartures();
  const index = list.findIndex((existing) => existing.id === departure.id);
  if (index < 0) list.push(departure);
  else list[index] = departure;
}

/** The status the seats give a departure — the server's DepartureStatusRecalculator, in small. */
export function statusOf(departure: StoredDeparture): DepartureStatus {
  if (departure.manualState) return departure.manualState;

  const taken = departure.capacityConfirmed + departure.capacityReserved;
  if (taken >= departure.capacityTotal) return 'SoldOut';
  if (taken >= departure.capacityTotal * 0.85) return 'NearlyFull';
  if (departure.isGroupDeparture && departure.capacityConfirmed >= departure.minPax)
    return 'Guaranteed';
  if (!departure.isGroupDeparture) return 'Guaranteed';
  return 'Open';
}

export function productTitleOf(productId: string): string {
  return findProduct(productId)?.title ?? 'A removed product';
}

/** For tests: start again from the seed. */
export function resetDepartureStore(): void {
  departures = null;
}
