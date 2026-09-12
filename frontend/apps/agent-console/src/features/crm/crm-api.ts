import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import type {
  Communication,
  CommunicationRequest,
  Customer,
  CustomerSummary,
  Lead,
  LeadRequest,
  LeadStage,
  LeadSummary,
  Quote,
  QuoteRequest,
  Task,
  TaskRequest,
} from './types';

/**
 * What the CRM screens need from the server, as one port. The stand-in in
 * `mock/` sits behind it until the CRM API (build plan F7) lands; then an HTTP
 * adapter replaces it in `main.tsx` and no screen changes.
 */
export interface CrmApi {
  listLeads(): Promise<LeadSummary[]>;
  getLead(id: string): Promise<Lead>;
  createLead(request: LeadRequest): Promise<Lead>;
  /** Lost needs a reason: it is what the agency learns from. */
  moveLead(id: string, stage: LeadStage, reason: string | null): Promise<Lead>;
  getQuote(id: string): Promise<Quote>;
  createQuote(leadId: string, request: QuoteRequest): Promise<Quote>;
  /** Drafts only. A sent quote is what the customer saw, and it stays that way. */
  saveQuote(id: string, request: QuoteRequest): Promise<Quote>;
  sendQuote(id: string): Promise<Quote>;
  listCustomers(): Promise<CustomerSummary[]>;
  getCustomer(id: string): Promise<Customer>;
  listTasks(filter: { open: boolean }): Promise<Task[]>;
  addTask(request: TaskRequest): Promise<Task>;
  completeTask(id: string): Promise<Task>;
  logCommunication(request: CommunicationRequest): Promise<Communication>;
}

const CrmApiContext = createContext<CrmApi | null>(null);

export const CrmApiProvider = CrmApiContext.Provider;

function useCrmApi(): CrmApi {
  const api = useContext(CrmApiContext);
  if (!api) throw new Error('useCrmApi must be used inside a <CrmApiProvider>.');
  return api;
}

function useAgencyId(): string {
  return useCurrentUser().agency?.id ?? 'none';
}

/** Everything under one agency key: a change anywhere refreshes every CRM screen that shows it. */
export const crmKeys = {
  all: (agencyId: string) => ['crm', agencyId] as const,
  leads: (agencyId: string) => [...crmKeys.all(agencyId), 'leads'] as const,
  lead: (agencyId: string, id: string) => [...crmKeys.all(agencyId), 'lead', id] as const,
  quote: (agencyId: string, id: string) => [...crmKeys.all(agencyId), 'quote', id] as const,
  customers: (agencyId: string) => [...crmKeys.all(agencyId), 'customers'] as const,
  customer: (agencyId: string, id: string) => [...crmKeys.all(agencyId), 'customer', id] as const,
  tasks: (agencyId: string, open: boolean) => [...crmKeys.all(agencyId), 'tasks', open] as const,
};

export function useLeads() {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  // New leads arrive from the storefront while the inbox is open.
  return useQuery({
    queryKey: crmKeys.leads(agencyId),
    queryFn: () => api.listLeads(),
    refetchInterval: 60_000,
  });
}

export function useLead(id: string) {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  return useQuery({ queryKey: crmKeys.lead(agencyId, id), queryFn: () => api.getLead(id) });
}

export function useQuote(id: string) {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  return useQuery({ queryKey: crmKeys.quote(agencyId, id), queryFn: () => api.getQuote(id) });
}

export function useCustomers() {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  return useQuery({ queryKey: crmKeys.customers(agencyId), queryFn: () => api.listCustomers() });
}

export function useCustomer(id: string) {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  return useQuery({ queryKey: crmKeys.customer(agencyId, id), queryFn: () => api.getCustomer(id) });
}

export function useTasks(open: boolean) {
  const api = useCrmApi();
  const agencyId = useAgencyId();
  return useQuery({
    queryKey: crmKeys.tasks(agencyId, open),
    queryFn: () => api.listTasks({ open }),
  });
}

/** A mutation that refreshes the whole CRM when it succeeds: leads, quotes, customers and tasks all show each other. */
function useCrmMutation<TInput, TResult>(run: (api: CrmApi, input: TInput) => Promise<TResult>) {
  const api = useCrmApi();
  const queryClient = useQueryClient();
  const agencyId = useAgencyId();

  return useMutation({
    mutationFn: (input: TInput) => run(api, input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: crmKeys.all(agencyId) }),
  });
}

export const useCreateLead = () =>
  useCrmMutation((api, request: LeadRequest) => api.createLead(request));

export const useMoveLead = () =>
  useCrmMutation((api, input: { id: string; stage: LeadStage; reason: string | null }) =>
    api.moveLead(input.id, input.stage, input.reason),
  );

/** Creates the quote the first time it is saved, and saves the draft after that. */
export const useSaveQuote = () =>
  useCrmMutation((api, input: { leadId: string; quoteId: string | null; request: QuoteRequest }) =>
    input.quoteId
      ? api.saveQuote(input.quoteId, input.request)
      : api.createQuote(input.leadId, input.request),
  );

export const useSendQuote = () => useCrmMutation((api, id: string) => api.sendQuote(id));

export const useAddTask = () => useCrmMutation((api, request: TaskRequest) => api.addTask(request));

export const useCompleteTask = () => useCrmMutation((api, id: string) => api.completeTask(id));

export const useLogCommunication = () =>
  useCrmMutation((api, request: CommunicationRequest) => api.logCommunication(request));
