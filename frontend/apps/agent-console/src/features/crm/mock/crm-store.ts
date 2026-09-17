import type {
  Communication,
  CustomerBooking,
  CustomerInvoice,
  CustomerKind,
  CustomerRef,
  CustomerTraveller,
  LeadSource,
  LeadStage,
  Quote,
  StageChange,
  Task,
} from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the CRM API lands (F7).
 * ============================================================================
 *
 * The stand-in's CRM, in memory: eight leads across every stage — two from
 * the website's trip-request form this week, quotes out and viewed, one won
 * and one lost — with the tasks and messages around them.
 */

export interface StoredLead {
  id: string;
  customerId: string;
  source: LeadSource;
  destination: string;
  travelFrom: string | null;
  travelTo: string | null;
  adults: number;
  children: number;
  budgetMinMinor: number | null;
  budgetMaxMinor: number | null;
  message: string;
  stage: LeadStage;
  lostReason: string | null;
  ownerName: string | null;
  createdAt: string;
  history: StageChange[];
}

export interface StoredCustomer extends CustomerRef {
  kind: CustomerKind;
  createdAt: string;
  /** When they last booked. Null with no bookings. */
  lastBookedAt: string | null;
  bookings: CustomerBooking[];
  invoices: CustomerInvoice[];
  travellers: CustomerTraveller[];
}

export interface CrmData {
  customers: StoredCustomer[];
  leads: StoredLead[];
  quotes: Quote[];
  tasks: Task[];
  communications: Communication[];
}

const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();
const hoursFromNow = (hours: number) => new Date(Date.now() + hours * 3_600_000).toISOString();
const dayFromToday = (days: number) =>
  new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);

function invoice(
  invoiceNumber: string,
  status: CustomerInvoice['status'],
  amountMinor: number,
  daysAgo: number,
  bookingReference: string,
): Omit<CustomerInvoice, 'customerName'> {
  return {
    id: `inv-${invoiceNumber}`,
    invoiceNumber,
    amountMinor,
    currency: 'NGN',
    status,
    issuedAt: hoursAgo(daysAgo * 24),
    bookingReference,
  };
}

function traveller(
  id: string,
  title: CustomerTraveller['title'],
  name: string,
  email: string | null,
  dateOfBirth: string,
  gender: CustomerTraveller['gender'],
): CustomerTraveller {
  return { id, title, name, email, dateOfBirth, gender };
}

function seed(): CrmData {
  const customer = (
    id: string,
    name: string,
    email: string | null,
    phone: string | null,
    days: number,
    bookings: CustomerBooking[] = [],
    extra: Pick<Partial<StoredCustomer>, 'kind' | 'invoices' | 'travellers'> = {},
  ): StoredCustomer => ({
    id,
    name,
    email,
    phone,
    kind: extra.kind ?? 'individual',
    createdAt: hoursAgo(days * 24),
    lastBookedAt:
      bookings
        .map((booking) => booking.bookedAt)
        .filter((at): at is string => Boolean(at))
        .sort()
        .at(-1) ?? null,
    bookings,
    invoices: extra.invoices ?? [],
    travellers: extra.travellers ?? [],
  });

  /** A flight or bus trip: travels `travelInDays` from today (negative is past), booked `bookedDaysAgo`. */
  const trip = (
    reference: string,
    product: 'flight' | 'bus',
    [from, fromCode]: [string, string],
    [to, toCode]: [string, string],
    carrier: string,
    { bookedDaysAgo, travelInDays }: { bookedDaysAgo: number; travelInDays: number },
    amountMinor: number,
  ): CustomerBooking => ({
    reference,
    title: `${from} → ${to}, ${carrier}`,
    travelDate: dayFromToday(travelInDays),
    status: 'Ticketed',
    amountMinor,
    product,
    route: { from, to, fromCode, toCode },
    carrier,
    bookedAt: hoursAgo(bookedDaysAgo * 24 + 3),
  });

  const LAGOS: [string, string] = ['Lagos', 'LOS'];
  const ABUJA: [string, string] = ['Abuja', 'ABV'];

  const customers: StoredCustomer[] = [
    customer('c-chiamaka', 'Chiamaka Okonkwo', 'chiamaka.okonkwo@example.test', '0803 214 5521', 0),
    customer('c-ibrahim', 'Ibrahim Sule', 'ibrahim.sule@example.test', '0806 771 2090', 1),
    customer('c-martins', 'Adeola Martins', 'adeola.martins@example.test', '0809 115 3348', 6),
    customer('c-grace', 'Grace Eze', 'grace.eze@example.test', '0802 660 1187', 12, [
      trip(
        'TRP-8K2N7C',
        'flight',
        LAGOS,
        ABUJA,
        'Air Peace',
        { bookedDaysAgo: 9, travelInDays: -5 },
        14_250_000,
      ),
    ]),
    customer('c-tosin', 'Tosin Bello', 'tosin.bello@example.test', '0805 332 9004', 30, [
      {
        // A tour: no product, route or carrier — it shows under "All" by its title.
        reference: 'TRP-8K2Q4M',
        title: 'Cape Town and the Garden Route, 12 travellers',
        travelDate: dayFromToday(35),
        status: 'Confirmed',
        amountMinor: 3_180_000_000,
        bookedAt: hoursAgo(25 * 24),
      },
      trip(
        'TRP-8K1Z2A',
        'flight',
        LAGOS,
        ['Accra', 'ACC'],
        'Ibom Air',
        { bookedDaysAgo: 4, travelInDays: 10 },
        19_800_000,
      ),
    ]),
    customer('c-uche', 'Uche Nnamdi', 'uche.nnamdi@example.test', null, 20),
    customer('c-halima', 'Halima Abubakar', 'halima.abubakar@example.test', '0807 404 8812', 3),
    customer('c-kelechi', 'Kelechi Obi', null, '0810 550 7713', 2),
    customer(
      'c-harbour',
      'Harbour Point Logistics',
      'travel@harbourpoint.example.test',
      '+234 901 220 4410',
      45,
      [
        trip(
          'TRP-8K2R1D',
          'flight',
          LAGOS,
          ABUJA,
          'Air Peace',
          { bookedDaysAgo: 2, travelInDays: 0 },
          28_500_000,
        ),
        trip(
          'TRP-8K2P5E',
          'bus',
          ['Enugu', 'ENU'],
          LAGOS,
          'Libra Motors',
          { bookedDaysAgo: 5, travelInDays: 6 },
          4_200_000,
        ),
        trip(
          'TRP-8K1X9Q',
          'flight',
          LAGOS,
          ABUJA,
          'Air Peace',
          { bookedDaysAgo: 12, travelInDays: -8 },
          31_050_000,
        ),
        trip(
          'TRP-8K0V6B',
          'flight',
          LAGOS,
          ['Warri', 'QRW'],
          'Air Peace',
          { bookedDaysAgo: 20, travelInDays: -15 },
          19_800_000,
        ),
        trip(
          'TRP-8K0T3H',
          'bus',
          ['Kano', 'KAN'],
          LAGOS,
          'Libra Motors',
          { bookedDaysAgo: 30, travelInDays: -25 },
          3_850_000,
        ),
        trip(
          'TRP-8JZY7L',
          'flight',
          LAGOS,
          ['London', 'LHR'],
          'British Airways',
          { bookedDaysAgo: 40, travelInDays: 21 },
          1_284_000_000,
        ),
      ],
      {
        kind: 'business',
        invoices: [
          invoice('INV-2026-0418', 'overdue', 31_050_000, 12, 'TRP-8K1X9Q'),
          invoice('INV-2026-0431', 'pending', 4_200_000, 5, 'TRP-8K2P5E'),
          invoice('INV-2026-0402', 'paid', 19_800_000, 20, 'TRP-8K0V6B'),
          invoice('INV-2026-0437', 'draft', 28_500_000, 2, 'TRP-8K2R1D'),
        ].map((raw) => ({ ...raw, customerName: 'Harbour Point Logistics' })),
        travellers: [
          traveller(
            't-1',
            'Mr',
            'Chinedu Okafor',
            'chinedu.okafor@harbourpoint.example.test',
            '1986-03-14',
            'Male',
          ),
          traveller(
            't-2',
            'Mrs',
            'Funmilayo Adebayo',
            'funmi.adebayo@harbourpoint.example.test',
            '1990-11-02',
            'Female',
          ),
          traveller(
            't-3',
            'Mr',
            'Musa Danjuma',
            'musa.danjuma@harbourpoint.example.test',
            '1979-07-21',
            'Male',
          ),
          traveller('t-4', 'Ms', 'Ifeoma Nwosu', null, '1994-01-30', 'Female'),
        ],
      },
    ),
    customer(
      'c-kanem',
      'Kanem Energy',
      'admin@kanemenergy.example.test',
      '+234 908 773 1602',
      75,
      [
        trip(
          'TRP-8JZW3P',
          'flight',
          ABUJA,
          ['Dubai', 'DXB'],
          'Emirates',
          { bookedDaysAgo: 18, travelInDays: -14 },
          816_500_000,
        ),
      ],
      { kind: 'business' },
    ),
  ];

  const lead = (
    id: string,
    customerId: string,
    source: LeadSource,
    stage: LeadStage,
    hours: number,
    trip: Partial<StoredLead>,
  ): StoredLead => ({
    id,
    customerId,
    source,
    destination: '',
    travelFrom: null,
    travelTo: null,
    adults: 2,
    children: 0,
    budgetMinMinor: null,
    budgetMaxMinor: null,
    message: '',
    lostReason: null,
    ownerName: 'Owner',
    ...trip,
    stage,
    createdAt: hoursAgo(hours),
    history: [
      {
        stage: 'New',
        at: hoursAgo(hours),
        byName: source === 'Manual' ? 'Owner' : 'Website',
        reason: null,
      },
    ],
  });

  const leads: StoredLead[] = [
    lead('l-dubai', 'c-chiamaka', 'TripRequestWidget', 'New', 2, {
      destination: 'Dubai',
      travelFrom: dayFromToday(98),
      travelTo: dayFromToday(104),
      budgetMaxMinor: 450_000_000,
      message:
        'Shopping trip before Christmas with my sister. 4-star hotel near Dubai Mall, and a desert safari if the budget allows.',
      ownerName: null,
    }),
    lead('l-umrah', 'c-ibrahim', 'TripRequestWidget', 'New', 26, {
      destination: 'Madinah and Makkah',
      travelFrom: dayFromToday(150),
      travelTo: dayFromToday(164),
      adults: 4,
      budgetMaxMinor: 1_200_000_000,
      message:
        'Umrah for my parents and my wife and me. Flights from Abuja please, hotels close to the Haram.',
      ownerName: null,
    }),
    lead('l-zanzibar', 'c-martins', 'ContactForm', 'Quoted', 140, {
      destination: 'Zanzibar',
      travelFrom: dayFromToday(70),
      travelTo: dayFromToday(77),
      budgetMinMinor: 300_000_000,
      budgetMaxMinor: 400_000_000,
      message: 'Honeymoon! Something romantic on the beach, we fly from Lagos.',
    }),
    lead('l-obudu', 'c-grace', 'Manual', 'Negotiating', 280, {
      destination: 'Obudu',
      travelFrom: dayFromToday(30),
      travelTo: dayFromToday(33),
      children: 2,
      budgetMaxMinor: 120_000_000,
      message: 'Family weekend. Asked on the phone whether the chalets have two bedrooms.',
    }),
    lead('l-capetown', 'c-tosin', 'Manual', 'Won', 700, {
      destination: 'Cape Town',
      travelFrom: dayFromToday(35),
      travelTo: dayFromToday(42),
      adults: 12,
      budgetMaxMinor: 3_300_000_000,
      message: 'Company retreat for the leadership team.',
    }),
    lead('l-london', 'c-uche', 'TripRequestWidget', 'Lost', 480, {
      destination: 'London',
      travelFrom: dayFromToday(20),
      travelTo: dayFromToday(34),
      adults: 1,
      budgetMaxMinor: 250_000_000,
      message: 'Visa and flights for a conference.',
      lostReason: 'Booked with another agent who was ₦180,000 cheaper on the flight.',
    }),
    lead('l-safari', 'c-halima', 'TripRequestWidget', 'Quoted', 70, {
      destination: 'Maasai Mara, Kenya',
      travelFrom: dayFromToday(120),
      travelTo: dayFromToday(126),
      budgetMaxMinor: 600_000_000,
      message: 'First safari. We would love a lodge with a view and a balloon ride.',
    }),
    lead('l-accra', 'c-kelechi', 'Manual', 'New', 44, {
      destination: 'Accra by road',
      travelFrom: dayFromToday(16),
      travelTo: dayFromToday(19),
      adults: 3,
      budgetMaxMinor: 60_000_000,
      message: 'Called in. Three friends, weekend in Accra, want the bus and a mid-range hotel.',
    }),
  ];

  const moved = (
    target: StoredLead,
    stage: LeadStage,
    hours: number,
    reason: string | null = null,
  ) => {
    target.history.push({ stage, at: hoursAgo(hours), byName: 'Owner', reason });
  };
  const byId = (id: string) => leads.find((l) => l.id === id)!;
  moved(byId('l-zanzibar'), 'Quoted', 120);
  moved(byId('l-obudu'), 'Quoted', 250);
  moved(byId('l-obudu'), 'Negotiating', 200);
  moved(byId('l-capetown'), 'Quoted', 650);
  moved(byId('l-capetown'), 'Won', 600);
  moved(byId('l-london'), 'Lost', 400, byId('l-london').lostReason);
  moved(byId('l-safari'), 'Quoted', 48);

  const ref = (id: string): CustomerRef => {
    const found = customers.find((c) => c.id === id)!;
    return { id: found.id, name: found.name, email: found.email, phone: found.phone };
  };

  const quotes: Quote[] = [
    {
      id: 'q-zanzibar',
      quoteNumber: 'QT-0007',
      leadId: 'l-zanzibar',
      customer: ref('c-martins'),
      title: 'Zanzibar honeymoon for 2 adults',
      status: 'Viewed',
      validUntil: dayFromToday(3),
      currency: 'NGN',
      items: [
        {
          description: 'Zanzibar Beach Escape, double room',
          quantity: 2,
          unitPriceMinor: 145_000_000,
          productId: null,
        },
        {
          description: 'Return flights Lagos – Zanzibar',
          quantity: 2,
          unitPriceMinor: 38_000_000,
          productId: null,
        },
        {
          description: 'Sunset dhow cruise with dinner',
          quantity: 2,
          unitPriceMinor: 6_500_000,
          productId: null,
        },
      ],
      itinerary: [
        {
          dayNumber: 1,
          title: 'Arrive in Zanzibar',
          description: 'Met at the airport and driven to Nungwi.',
        },
        { dayNumber: 2, title: 'Stone Town', description: 'A guided morning in Stone Town.' },
      ],
      notes: 'Prices held until the date above. Flights are quoted, not held.',
      totalMinor: 0,
      publicUrl: 'https://lekki-horizon.example/q/7Hq2c',
      sentAt: hoursAgo(120),
      viewedAt: hoursAgo(20),
      respondedAt: null,
    },
    {
      id: 'q-obudu',
      quoteNumber: 'QT-0005',
      leadId: 'l-obudu',
      customer: ref('c-grace'),
      title: 'Obudu family weekend',
      status: 'Sent',
      validUntil: dayFromToday(2),
      currency: 'NGN',
      items: [
        {
          description: 'Obudu Mountain Resort Weekend, adult',
          quantity: 2,
          unitPriceMinor: 38_500_000,
          productId: null,
        },
        {
          description: 'Obudu Mountain Resort Weekend, child 2–11',
          quantity: 2,
          unitPriceMinor: 21_000_000,
          productId: null,
        },
      ],
      itinerary: [],
      notes: '',
      totalMinor: 0,
      publicUrl: 'https://lekki-horizon.example/q/2Xk9p',
      sentAt: hoursAgo(250),
      viewedAt: null,
      respondedAt: null,
    },
    {
      id: 'q-capetown',
      quoteNumber: 'QT-0002',
      leadId: 'l-capetown',
      customer: ref('c-tosin'),
      title: 'Leadership retreat, Cape Town',
      status: 'Accepted',
      validUntil: dayFromToday(-20),
      currency: 'NGN',
      items: [
        {
          description: 'Cape Town and the Garden Route, 8 days',
          quantity: 12,
          unitPriceMinor: 265_000_000,
          productId: null,
        },
      ],
      itinerary: [],
      notes: '',
      totalMinor: 0,
      publicUrl: 'https://lekki-horizon.example/q/9Lm4t',
      sentAt: hoursAgo(650),
      viewedAt: hoursAgo(640),
      respondedAt: hoursAgo(600),
    },
    {
      id: 'q-safari',
      quoteNumber: 'QT-0008',
      leadId: 'l-safari',
      customer: ref('c-halima'),
      title: 'Maasai Mara, 2 adults',
      status: 'Sent',
      validUntil: dayFromToday(5),
      currency: 'NGN',
      items: [
        {
          description: 'Four nights at a lodge on the Mara, full board',
          quantity: 2,
          unitPriceMinor: 185_000_000,
          productId: null,
        },
        {
          description: 'Balloon safari at sunrise',
          quantity: 2,
          unitPriceMinor: 32_000_000,
          productId: null,
        },
      ],
      itinerary: [],
      notes: '',
      totalMinor: 0,
      publicUrl: 'https://lekki-horizon.example/q/4Rt8w',
      sentAt: hoursAgo(48),
      viewedAt: null,
      respondedAt: null,
    },
  ];
  for (const quote of quotes) {
    quote.totalMinor = quote.items.reduce(
      (sum, item) => sum + item.quantity * item.unitPriceMinor,
      0,
    );
  }

  const task = (
    id: string,
    title: string,
    dueAt: string,
    related: Task['related'],
    completedAt: string | null = null,
  ): Task => ({
    id,
    title,
    dueAt,
    completedAt,
    related,
    ownerName: 'Owner',
  });

  const tasks: Task[] = [
    task('t-1', 'Call Chiamaka about hotel preferences', hoursFromNow(3), {
      type: 'Lead',
      id: 'l-dubai',
      label: 'Chiamaka Okonkwo · Dubai',
    }),
    task('t-2', 'Send Umrah options with Abuja flights', hoursFromNow(26), {
      type: 'Lead',
      id: 'l-umrah',
      label: 'Ibrahim Sule · Madinah and Makkah',
    }),
    task('t-3', 'Follow up on the Zanzibar quote — they have opened it', hoursAgo(20), {
      type: 'Quote',
      id: 'q-zanzibar',
      label: 'QT-0007 · Adeola Martins',
    }),
    task('t-4', 'Confirm two-bedroom chalets with the resort', hoursFromNow(72), {
      type: 'Lead',
      id: 'l-obudu',
      label: 'Grace Eze · Obudu',
    }),
    task(
      't-5',
      'Collect passport copies for the retreat',
      hoursAgo(500),
      { type: 'Lead', id: 'l-capetown', label: 'Tosin Bello · Cape Town' },
      hoursAgo(490),
    ),
  ];

  const message = (
    id: string,
    related: Communication['related'],
    channel: Communication['channel'],
    direction: Communication['direction'],
    summary: string,
    hours: number,
    byName = 'Owner',
  ): Communication => ({
    id,
    channel,
    direction,
    summary,
    at: hoursAgo(hours),
    byName,
    related,
  });

  const communications: Communication[] = [
    message(
      'm-1',
      { type: 'Lead', id: 'l-dubai' },
      'Email',
      'Inbound',
      'Trip request from the website.',
      2,
      'Chiamaka Okonkwo',
    ),
    message(
      'm-2',
      { type: 'Lead', id: 'l-umrah' },
      'Email',
      'Inbound',
      'Trip request from the website.',
      26,
      'Ibrahim Sule',
    ),
    message(
      'm-3',
      { type: 'Lead', id: 'l-zanzibar' },
      'Email',
      'Outbound',
      'Sent quote QT-0007.',
      120,
    ),
    message(
      'm-4',
      { type: 'Lead', id: 'l-zanzibar' },
      'Whatsapp',
      'Inbound',
      'Asked whether the dhow cruise can be moved to their last night.',
      30,
      'Adeola Martins',
    ),
    message(
      'm-5',
      { type: 'Lead', id: 'l-obudu' },
      'Call',
      'Inbound',
      'Wants two-bedroom chalets; would pay a little more for them.',
      210,
      'Grace Eze',
    ),
    message(
      'm-6',
      { type: 'Lead', id: 'l-capetown' },
      'Note',
      'Outbound',
      'Deposit received. Rooming list due a month before.',
      590,
    ),
    message(
      'm-7',
      { type: 'Lead', id: 'l-london' },
      'Call',
      'Outbound',
      'Called back; they had already booked elsewhere.',
      400,
    ),
    message(
      'm-8',
      { type: 'Lead', id: 'l-safari' },
      'Email',
      'Outbound',
      'Sent quote QT-0008.',
      48,
    ),
    message(
      'm-9',
      { type: 'Lead', id: 'l-accra' },
      'Call',
      'Inbound',
      'Walked through bus times Lagos to Accra.',
      44,
      'Kelechi Obi',
    ),
  ];

  return { customers, leads, quotes, tasks, communications };
}

let data: CrmData | null = null;

export function crmData(): CrmData {
  return (data ??= seed());
}

/** For tests: start again from the seed. */
export function resetCrmStore(): void {
  data = null;
}
