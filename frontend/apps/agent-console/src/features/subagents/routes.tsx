import type { RouteObject } from 'react-router-dom';
import { NetworkPerformancePage } from './pages/network-performance-page';
import { SubAgentDetailPage } from './pages/sub-agent-detail-page';
import { SubAgentsPage } from './pages/sub-agents-page';

/**
 * Issue 63 — the sub-agent network.
 *
 * `/network` is open to a sub-agent too: it sees only its own figures, which is
 * the server's rule rather than this file's. `/sub-agents` is a principal's
 * screen, and the API refuses it for anybody else.
 */
export const subAgentRoutes: RouteObject[] = [
  { path: '/sub-agents', element: <SubAgentsPage /> },
  { path: '/sub-agents/:subAgencyId', element: <SubAgentDetailPage /> },
  { path: '/network', element: <NetworkPerformancePage /> },
];
