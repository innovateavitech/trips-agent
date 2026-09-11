import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import type {
  Category,
  CategoryType,
  Product,
  ProductRequest,
  ProductSummary,
  ProductTransition,
  UploadedImage,
} from './types';

/**
 * What the catalog screens need from the server, as one port — the same shape
 * as `SearchApi`. The stand-in in `mock/` sits behind it until the product API
 * (#161) lands; then an HTTP adapter over the generated client replaces it, in
 * `main.tsx`, and no screen changes.
 */
export interface CatalogApi {
  listProducts(): Promise<ProductSummary[]>;
  getProduct(id: string): Promise<Product>;
  createProduct(request: ProductRequest): Promise<Product>;
  /**
   * Saves the whole product. A draft saves whatever state it is in — nothing is
   * checked until it is published. A published product refuses a save that
   * would leave it unpublishable, so the storefront never shows a broken one.
   */
  saveProduct(id: string, request: ProductRequest): Promise<Product>;
  /** Publishing refuses with 422 while the product has publish problems. */
  transition(id: string, action: ProductTransition): Promise<Product>;
  listCategories(): Promise<Category[]>;
  createCategory(input: { name: string; type: CategoryType }): Promise<Category>;
  /** Uploads an image and returns it once it is safe to attach. */
  uploadImage(file: File): Promise<UploadedImage>;
}

const CatalogApiContext = createContext<CatalogApi | null>(null);

export const CatalogApiProvider = CatalogApiContext.Provider;

function useCatalogApi(): CatalogApi {
  const api = useContext(CatalogApiContext);
  if (!api) throw new Error('useCatalogApi must be used inside a <CatalogApiProvider>.');
  return api;
}

/** Keyed by agency, so switching agency never shows the last one's products. */
export const catalogKeys = {
  all: ['catalog'] as const,
  products: (agencyId: string) => [...catalogKeys.all, agencyId, 'products'] as const,
  product: (agencyId: string, id: string) => [...catalogKeys.all, agencyId, 'product', id] as const,
  categories: (agencyId: string) => [...catalogKeys.all, agencyId, 'categories'] as const,
};

function useAgencyId(): string {
  return useCurrentUser().agency?.id ?? 'none';
}

export function useProducts() {
  const api = useCatalogApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: catalogKeys.products(agencyId),
    queryFn: () => api.listProducts(),
  });
}

export function useProduct(id: string) {
  const api = useCatalogApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: catalogKeys.product(agencyId, id),
    queryFn: () => api.getProduct(id),
  });
}

export function useCategories() {
  const api = useCatalogApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: catalogKeys.categories(agencyId),
    queryFn: () => api.listCategories(),
    // An agency's categories change rarely, and only from this console.
    staleTime: 5 * 60_000,
  });
}

/** After any change: the product's own entry is the answer, and the list is out of date. */
function useProductChanged() {
  const queryClient = useQueryClient();
  const agencyId = useAgencyId();

  return (product: Product) => {
    queryClient.setQueryData(catalogKeys.product(agencyId, product.id), product);
    void queryClient.invalidateQueries({ queryKey: catalogKeys.products(agencyId) });
  };
}

/** Creates the product the first time it is saved, and saves it after that. */
export function useSaveProduct() {
  const api = useCatalogApi();
  const changed = useProductChanged();

  return useMutation({
    mutationFn: ({ id, request }: { id: string | null; request: ProductRequest }) =>
      id ? api.saveProduct(id, request) : api.createProduct(request),
    onSuccess: changed,
  });
}

export function useProductTransition(id: string) {
  const api = useCatalogApi();
  const changed = useProductChanged();
  const queryClient = useQueryClient();
  const agencyId = useAgencyId();

  return useMutation({
    mutationFn: (action: ProductTransition) => api.transition(id, action),
    onSuccess: changed,
    // A refused publish means the checklist is out of date: fetch the server's reasons.
    onError: () => void queryClient.invalidateQueries({ queryKey: catalogKeys.product(agencyId, id) }),
  });
}

export function useCreateCategory() {
  const api = useCatalogApi();
  const queryClient = useQueryClient();
  const agencyId = useAgencyId();

  return useMutation({
    mutationFn: (input: { name: string; type: CategoryType }) => api.createCategory(input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: catalogKeys.categories(agencyId) }),
  });
}

export function useUploadImage() {
  const api = useCatalogApi();

  return useMutation({ mutationFn: (file: File) => api.uploadImage(file) });
}
