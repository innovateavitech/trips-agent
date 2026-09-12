import type { RouteObject } from 'react-router-dom';
import { AddressesPage } from './pages/addresses-page';
import { DesignPage } from './pages/design-page';
import { PageEditorPage } from './pages/page-editor-page';
import { WebsitePage } from './pages/website-page';

/** Issues 58 and 59 — the website builder, its design, its pages and its web addresses. */
export const storefrontRoutes: RouteObject[] = [
  { path: '/website', element: <WebsitePage /> },
  { path: '/website/design', element: <DesignPage /> },
  { path: '/website/addresses', element: <AddressesPage /> },
  { path: '/website/pages/:pageId', element: <PageEditorPage /> },
];
