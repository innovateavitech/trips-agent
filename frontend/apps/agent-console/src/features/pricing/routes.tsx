import type { RouteObject } from 'react-router-dom';
import { PricingPage } from './pages/pricing-page';

/** Issue #55 — markup rules, and the "which rule wins" explainer. */
export const pricingRoutes: RouteObject[] = [{ path: '/pricing', element: <PricingPage /> }];
