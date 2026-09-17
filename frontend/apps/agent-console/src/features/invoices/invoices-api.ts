import { createContext, useContext } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CustomerInvoice } from './types';

/**
 * The invoices port. Mock-backed for this first pass — see `types.ts` for
 * why this is a new feature rather than a rename of Billing.
 */
export interface InvoicesApi {
  listInvoices(): Promise<CustomerInvoice[]>;
}

const InvoicesApiContext = createContext<InvoicesApi | null>(null);

export const InvoicesApiProvider = InvoicesApiContext.Provider;

function useInvoicesApi(): InvoicesApi {
  const api = useContext(InvoicesApiContext);
  if (!api) throw new Error('useInvoicesApi must be used inside an <InvoicesApiProvider>.');
  return api;
}

export const invoiceKeys = {
  all: ['invoices'] as const,
  list: () => [...invoiceKeys.all, 'list'] as const,
};

export function useInvoices() {
  const api = useInvoicesApi();
  return useQuery({
    queryKey: invoiceKeys.list(),
    queryFn: () => api.listInvoices(),
  });
}
