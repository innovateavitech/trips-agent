import type { RouteObject } from 'react-router-dom';
import { Bus, Plane } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/** Issue #52 — flight and bus search. Replace the placeholders with the real pages. */
export const searchRoutes: RouteObject[] = [
  {
    path: '/search/flights',
    element: (
      <PlannedScreen
        title="Flights"
        description="Search domestic and international fares at your net rate."
        issue={52}
        icon={Plane}
        capabilities={[
          'Search one-way and return, for adults, children and infants',
          'Compare Air Peace, Ibom Air, Arik, United Nigeria and international carriers',
          'See your net rate and your sell price side by side',
          'Confirm a fare and see its ticket time limit before you book',
        ]}
      />
    ),
  },
  {
    path: '/search/buses',
    element: (
      <PlannedScreen
        title="Buses"
        description="Search intercity coaches across Nigeria."
        issue={52}
        icon={Bus}
        capabilities={[
          'Search Lagos, Abuja, Port Harcourt, Enugu and more by date',
          'Compare GIG Mobility, ABC Transport and other operators',
          'Pick seats on the coach layout',
        ]}
      />
    ),
  },
];
