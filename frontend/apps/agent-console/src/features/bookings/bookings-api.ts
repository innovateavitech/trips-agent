import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import { canViewMargin } from '../search/search-rules';
import type { BookingDetail, BookingListItem, ResolutionAction } from './types';

/**
 * Everything the bookings screens need from the server — the same
 * port-and-adapter shape as `WalletApi` and `SearchApi`. `main.tsx` puts the
 * stand-in in `mock/` behind it until the orders endpoints (#42, #44) exist.
 */
export interface BookingsApi {
  listBookings(): Promise<BookingListItem[]>;

  getBooking(reference: string): Promise<BookingDetail>;

  /** Only for a booking in the resolution queue. Substituting is a new search, not a call here. */
  resolve(
    reference: string,
    action: Exclude<ResolutionAction, 'substitute'>,
  ): Promise<BookingDetail>;
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
};

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
