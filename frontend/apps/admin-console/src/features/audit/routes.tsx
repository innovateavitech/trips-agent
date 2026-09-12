import type { RouteObject } from 'react-router-dom';
import { AUDIT_VIEW_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { AuditLogPage } from './pages/audit-page';

/**
 * Behind `audit.view`, which Super Admin, Operations and Finance hold and Support does not —
 * reading colleagues' actions is oversight work rather than support work.
 */
const REFUSAL = {
  title: 'Your account cannot open the audit log',
  detail:
    'Reading the audit trail needs the audit.view permission. Support accounts do not have it. ' +
    'Ask a Super Admin if your work needs it.',
};

export const auditRoutes: RouteObject[] = [
  {
    path: '/audit',
    element: (
      <RequirePermission permission={AUDIT_VIEW_PERMISSION} refusal={REFUSAL}>
        <AuditLogPage />
      </RequirePermission>
    ),
  },
];
