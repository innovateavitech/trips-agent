import type { RouteObject } from 'react-router-dom';
import { BookingFlowPage } from './pages/booking-flow-page';

/**
 * Issue #53 — the booking flow: travellers → review and pay → ticket.
 *
 * No sidebar entry on purpose: it is entered from a search result, never cold.
 * `/book/*` so a visit without a fare in hand still lands somewhere useful.
 */
export const bookingRoutes: RouteObject[] = [{ path: '/book/*', element: <BookingFlowPage /> }];
