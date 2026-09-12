import type { RouteObject } from 'react-router-dom';
import { DepartureEditorPage } from './pages/departure-editor-page';
import { DeparturePage } from './pages/departure-page';
import { DeparturesPage } from './pages/departures-page';

/** Build plan F6 — group departures: the list, one departure, and adding or changing one. */
export const departuresRoutes: RouteObject[] = [
  { path: '/departures', element: <DeparturesPage /> },
  { path: '/departures/new', element: <DepartureEditorPage /> },
  { path: '/departures/:departureId', element: <DeparturePage /> },
  { path: '/departures/:departureId/edit', element: <DepartureEditorPage /> },
];
