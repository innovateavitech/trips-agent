import { createContext, useContext } from 'react';
import type {
  StatementFilters,
  StatementPage,
  TopUpIntent,
  TopUpResult,
  WalletSummary,
  WalletTransaction,
} from './types';

/**
 * Everything these screens need from the server, as one interface.
 *
 * The screens depend on THIS, not on `fetch`, so when issue #26 ships the real
 * endpoints the change is one adapter — no component is touched. It is the same
 * ports-and-adapters idea the .NET side uses for `IBlobStorage`, applied to the
 * frontend for the same reason: the thing that is not built yet should not be
 * able to hold up the thing that is.
 */
export interface WalletApi {
  getSummary(): Promise<WalletSummary>;

  getStatement(filters: StatementFilters, page: number, pageSize: number): Promise<StatementPage>;

  /**
   * Every row matching the filters, for the CSV download.
   *
   * Separate from `getStatement` on purpose. Exporting "what is on screen"
   * gives the agent page 2 of 9 and no warning, which is worse than useless
   * when they are reconciling. The real implementation will stream this from
   * the server rather than walking the pages.
   */
  getStatementForExport(filters: StatementFilters): Promise<WalletTransaction[]>;

  /**
   * Registers the intended top-up and returns the gateway's hosted page.
   *
   * Returns a URL rather than card details because we never want a PAN near
   * this application — Paystack's hosted page keeps the card entirely out of
   * our scope (FRD §2.4 RS-4, "no PAN, ever").
   */
  startTopUp(amountMinor: number): Promise<TopUpIntent>;

  /**
   * The server's verdict on a top-up.
   *
   * The ONLY source of truth for whether money arrived. Paystack redirects the
   * agent back to us with a reference, but that redirect proves nothing — it
   * can be replayed, edited in the address bar, or fired before the webhook
   * lands. The wallet is credited server-side after server-side verification,
   * and this call just reports what happened (issue #26).
   */
  getTopUp(reference: string): Promise<TopUpResult>;
}

const WalletApiContext = createContext<WalletApi | null>(null);

export const WalletApiProvider = WalletApiContext.Provider;

export function useWalletApi(): WalletApi {
  const api = useContext(WalletApiContext);
  if (!api) {
    throw new Error('useWalletApi must be used inside a <WalletApiProvider>.');
  }
  return api;
}

/**
 * Query keys, in one place.
 *
 * TanStack Query invalidates by key prefix, so `['wallet']` clears everything
 * wallet-related after a successful top-up — which is what you want, because a
 * top-up changes both the balance and the statement.
 */
export const walletKeys = {
  all: ['wallet'] as const,
  summary: () => [...walletKeys.all, 'summary'] as const,
  statement: (filters: StatementFilters, page: number) =>
    [...walletKeys.all, 'statement', filters, page] as const,
  topUp: (reference: string) => [...walletKeys.all, 'top-up', reference] as const,
};
