import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import { canViewMargin } from '../search/search-rules';
import { isPreparing } from './documents-rules';
import type { BookingDetail, BookingDocument, BookingListItem, ResolutionAction } from './types';

/**
 * Everything the bookings screens need from the server — the same
 * port-and-adapter shape as `WalletApi` and `SearchApi`. `main.tsx` puts the
 * stand-in in `mock/` behind it until the orders endpoints (#42, #44) exist.
 *
 * The two document methods already have real endpoints (#46), and take and
 * return exactly their shapes: an HTTP adapter passes them straight through.
 */
export interface BookingsApi {
  listBookings(): Promise<BookingListItem[]>;

  getBooking(reference: string): Promise<BookingDetail>;

  /** Only for a booking in the resolution queue. Substituting is a new search, not a call here. */
  resolve(
    reference: string,
    action: Exclude<ResolutionAction, 'substitute'>,
  ): Promise<BookingDetail>;

  /**
   * The booking's invoices and vouchers, each with a signed download link once
   * its PDF is ready. `GET /api/v1/documents?orderReference={reference}`.
   */
  listDocuments(reference: string): Promise<BookingDocument[]>;

  /**
   * Replaces a document with a new issue under a new number, and emails the
   * customer the new copy; the original is kept exactly as it was.
   * `POST /api/v1/documents/{documentId}/reissue`.
   */
  reissueDocument(documentId: string): Promise<BookingDocument>;
}

const BookingsApiContext = createContext<BookingsApi | null>(null);

export const BookingsApiProvider = BookingsApiContext.Provider;

function useBookingsApi(): BookingsApi {
  const api = useContext(BookingsApiContext);
  if (!api) throw new Error('useBookingsApi must be used inside a <BookingsApiProvider>.');
  return api;
}

/** Keyed by agency, so one agency's bookings are never shown to the next. */
export const bookingsKeys = {
  all: ['bookings'] as const,
  list: (agencyId: string) => [...bookingsKeys.all, 'list', agencyId] as const,
  detail: (agencyId: string, reference: string) =>
    [...bookingsKeys.all, 'detail', agencyId, reference] as const,
  documents: (agencyId: string, reference: string) =>
    [...bookingsKeys.all, 'documents', agencyId, reference] as const,
};

/**
 * A booking's documents. Asked for again every few seconds while any is being
 * prepared — the Worker renders them just after the ticket lands — and not at
 * all once they are ready.
 */
export function useBookingDocuments(reference: string, { enabled = true } = {}) {
  const api = useBookingsApi();
  const user = useCurrentUser();

  return useQuery({
    queryKey: bookingsKeys.documents(user.agency?.id ?? 'none', reference),
    queryFn: () => api.listDocuments(reference),
    enabled,
    refetchInterval: (query) => (isPreparing(query.state.data) ? 3_000 : false),
    refetchIntervalInBackground: true,
  });
}

export function useReissueDocument(reference: string) {
  const api = useBookingsApi();
  const user = useCurrentUser();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (documentId: string) => api.reissueDocument(documentId),
    onSuccess: () => {
      // The list now holds the new issue, still being prepared, and shows the old one as replaced.
      void queryClient.invalidateQueries({
        queryKey: bookingsKeys.documents(user.agency?.id ?? 'none', reference),
      });
    },
  });
}

export function useBookings() {
  const api = useBookingsApi();
  const user = useCurrentUser();

  return useQuery({
    queryKey: bookingsKeys.list(user.agency?.id ?? 'none'),
    queryFn: () => api.listBookings(),
    // A ticket can land while the list is open; half a minute keeps it honest.
    refetchInterval: 30_000,
  });
}

export function useBooking(reference: string) {
  const api = useBookingsApi();
  const user = useCurrentUser();
  const canView = canViewMargin(user.roles);

  return useQuery({
    queryKey: bookingsKeys.detail(user.agency?.id ?? 'none', reference),
    queryFn: () => api.getBooking(reference),
    // Stripped before any component sees it, like search results.
    select: (booking) =>
      canView ? booking : { ...booking, price: { ...booking.price, margin: null } },
    // Waiting on the airline: check every few seconds, so "Ticketed" appears when it happens —
    // in a background tab too, which is where an agent waiting on a ticket usually is.
    refetchInterval: (query) => (query.state.data?.status === 'awaiting_ticket' ? 5_000 : false),
    refetchIntervalInBackground: true,
  });
}

export function useResolveBooking() {
  const api = useBookingsApi();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      reference,
      action,
    }: {
      reference: string;
      action: Exclude<ResolutionAction, 'substitute'>;
    }) => api.resolve(reference, action),
    onSuccess: () => {
      // A resolution changes the list, the dashboard's "needs attention", and — for a refund — the wallet.
      void queryClient.invalidateQueries({ queryKey: bookingsKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      void queryClient.invalidateQueries({ queryKey: ['wallet'] });
    },
  });
}
