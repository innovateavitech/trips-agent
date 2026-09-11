import type { RouteObject } from 'react-router-dom';
import { BookingDetailPage } from './pages/booking-detail-page';
import { BookingsPage } from './pages/bookings-page';
import { ResolutionPage } from './pages/resolution-page';

/** Issue #54 — bookings list and detail, and the resolution queue. */
export const bookingsRoutes: RouteObject[] = [
  { path: '/bookings', element: <BookingsPage /> },
  { path: '/bookings/:orderId', element: <BookingDetailPage /> },
  { path: '/resolution', element: <ResolutionPage /> },
];
