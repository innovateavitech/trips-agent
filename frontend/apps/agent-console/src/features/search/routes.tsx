import type { RouteObject } from 'react-router-dom';
import { BusSearchPage } from './pages/bus-search-page';
import { FlightSearchPage } from './pages/flight-search-page';

/** Issue #52 — flight and bus search. */
export const searchRoutes: RouteObject[] = [
  { path: '/search/flights', element: <FlightSearchPage /> },
  { path: '/search/buses', element: <BusSearchPage /> },
];
