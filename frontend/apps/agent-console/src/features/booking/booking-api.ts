import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import type {
  BookingDraft,
  BookingProgress,
  PlaceBookingInput,
  PlacedBooking,
  PriceConfirmation,
  TravellerDetails,
} from './types';

/**
 * What the booking flow needs from the server, as one port — the same shape as
 * `SearchApi`. The stand-in in `mock/` sits behind it until the checkout saga
 * (#42) exists; then an HTTP adapter replaces it and no component changes.
 */
export interface BookingFlowApi {
  /**
   * Asks the supplier to confirm the price for these travellers. The answer can
   * differ from the search, and carries the deadline to issue by.
   */
  confirmPrice(draft: BookingDraft, travellers: TravellerDetails[]): Promise<PriceConfirmation>;

  /** Pays and books. The same idempotency key twice is the same booking, never a second charge. */
  placeBooking(order: PlaceBookingInput): Promise<PlacedBooking>;

  /** Where the booking has got to with the supplier. There are no webhooks: this is polled. */
  getProgress(reference: string): Promise<BookingProgress>;
}

const BookingFlowApiContext = createContext<BookingFlowApi | null>(null);

export const BookingFlowApiProvider = BookingFlowApiContext.Provider;

function useBookingFlowApi(): BookingFlowApi {
  const api = useContext(BookingFlowApiContext);
  if (!api) throw new Error('useBookingFlowApi must be used inside a <BookingFlowApiProvider>.');
  return api;
}

export function useConfirmPrice() {
  const api = useBookingFlowApi();

  return useMutation({
    mutationFn: ({ draft, travellers }: { draft: BookingDraft; travellers: TravellerDetails[] }) =>
      api.confirmPrice(draft, travellers),
  });
}

export function usePlaceBooking() {
  const api = useBookingFlowApi();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (order: PlaceBookingInput) => api.placeBooking(order),
    onSuccess: () => {
      // A new booking changes the list, the dashboard, and — paid from the wallet — the balance.
      void queryClient.invalidateQueries({ queryKey: ['bookings'] });
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      void queryClient.invalidateQueries({ queryKey: ['wallet'] });
    },
  });
}

/**
 * Polled every two seconds while the ticket is pending, and not at all once it has settled.
 * It keeps polling in a background tab: an agent waiting on a ticket usually switches away,
 * and should come back to the answer rather than to a spinner.
 */
export function useBookingProgress(reference: string) {
  const api = useBookingFlowApi();

  return useQuery({
    queryKey: ['booking-progress', reference],
    queryFn: () => api.getProgress(reference),
    refetchInterval: (query) => (query.state.data?.status === 'awaiting_ticket' ? 2_000 : false),
    refetchIntervalInBackground: true,
  });
}
