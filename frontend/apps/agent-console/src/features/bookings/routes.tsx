import type { RouteObject } from 'react-router-dom';
import { LifeBuoy, Ticket } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/** Issue #54 — bookings list and detail, and the resolution queue. */
export const bookingsRoutes: RouteObject[] = [
  {
    path: '/bookings',
    element: (
      <PlannedScreen
        title="Bookings"
        description="Every booking your agency has made, and where each one stands."
        issue={54}
        icon={Ticket}
        capabilities={[
          'Filter by status, traveller, route and date',
          'Open a booking to see its PNR, tickets, payments and history',
          'Download the invoice and e-ticket in your own branding',
        ]}
      />
    ),
  },
  {
    path: '/bookings/:orderId',
    element: (
      <PlannedScreen
        title="Booking"
        description="One booking, in full."
        issue={54}
        icon={Ticket}
        capabilities={['See the PNR, tickets, payments and every status change']}
      />
    ),
  },
  {
    path: '/resolution',
    element: (
      <PlannedScreen
        title="Resolution queue"
        description="Bookings that need a decision from you."
        issue={54}
        icon={LifeBuoy}
        capabilities={[
          'See bookings the airline did not confirm, and why',
          'Retry, rebook or refund to the wallet, with the amount shown first',
        ]}
      />
    ),
  },
];
