import type { RouteObject } from 'react-router-dom';
import { InvoicesPage } from './pages/invoices-page';

export const invoicesRoutes: RouteObject[] = [{ path: '/invoices', element: <InvoicesPage /> }];
