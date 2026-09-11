import type { RouteObject } from 'react-router-dom';
import { CatalogPage } from './pages/catalog-page';
import { ProductEditorPage } from './pages/product-editor-page';

/** Epic #56 — the catalog list (#162) and the product editor (#162, #163). */
export const catalogRoutes: RouteObject[] = [
  { path: '/catalog', element: <CatalogPage /> },
  { path: '/catalog/new/:type', element: <ProductEditorPage /> },
  { path: '/catalog/:productId', element: <ProductEditorPage /> },
];
