import type { RouteObject } from 'react-router-dom';
import { ClipboardCheck } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/**
 * Issue #53 — the booking flow: travellers → review → confirm → ticketed.
 *
 * No sidebar entry on purpose: it is entered from a search result, never cold.
 * `/book/*` so the flow can own its own steps (`/book/travellers`, …).
 */
export const bookingRoutes: RouteObject[] = [
  {
    path: '/book/*',
    element: (
      <PlannedScreen
        title="Book"
        description="Take a confirmed fare through to a ticket."
        issue={53}
        icon={ClipboardCheck}
        capabilities={[
          'Enter traveller names and passport details',
          'Review the fare, your markup and the total before paying',
          'Pay from your wallet and watch the booking move to ticketed',
        ]}
      />
    ),
  },
];
