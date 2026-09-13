import type { RouteObject } from 'react-router-dom';
import { CustomerPage } from './pages/customer-page';
import { CustomersPage } from './pages/customers-page';
import { LeadPage } from './pages/lead-page';
import { LeadsPage } from './pages/leads-page';
import { NewLeadPage } from './pages/new-lead-page';
import { QuotePage } from './pages/quote-page';
import { TasksPage } from './pages/tasks-page';

/** Build plan F7 — the CRM: leads, quotes, customers and tasks. */
export const crmRoutes: RouteObject[] = [
  { path: '/crm/leads', element: <LeadsPage /> },
  { path: '/crm/leads/new', element: <NewLeadPage /> },
  { path: '/crm/leads/:leadId', element: <LeadPage /> },
  { path: '/crm/leads/:leadId/quotes/new', element: <QuotePage /> },
  { path: '/crm/quotes/:quoteId', element: <QuotePage /> },
  { path: '/crm/customers', element: <CustomersPage /> },
  { path: '/crm/customers/:customerId', element: <CustomerPage /> },
  { path: '/crm/tasks', element: <TasksPage /> },
];
