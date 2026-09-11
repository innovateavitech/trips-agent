import type { RouteObject } from 'react-router-dom';
import { SlidersHorizontal } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/** Issue #55 — markup rules. */
export const pricingRoutes: RouteObject[] = [
  {
    path: '/pricing',
    element: (
      <PlannedScreen
        title="Pricing rules"
        description="Decide what you add on top of the net rate."
        issue={55}
        icon={SlidersHorizontal}
        capabilities={[
          'Set a fixed or percentage markup by product, route or airline',
          'Preview the sell price a traveller would see before saving',
          'Changes apply to new bookings only; past prices never move',
        ]}
      />
    ),
  },
];
