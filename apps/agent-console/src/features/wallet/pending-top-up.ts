/**
 * A breadcrumb dropped before we send the agent to the payment gateway.
 *
 * Why it exists: a hosted gateway page is a one-way door. The agent leaves our
 * app entirely, and the three things that happen next are indistinguishable
 * from our side unless we wrote something down first —
 *
 *   1. they pay, and Paystack redirects them to our return URL;
 *   2. they close the tab halfway through;
 *   3. they hit Back, or reopen the console from a bookmark.
 *
 * In cases 2 and 3 nobody ever visits the return URL, so without this the
 * console would show a stale balance and no explanation. With it, the wallet
 * page can say "you started a top-up — here is what happened to it".
 *
 * `sessionStorage`, not `localStorage`: this is one tab's business, and it
 * should not survive the browser being closed and reopened next week.
 */

const KEY = 'trips.wallet.pendingTopUp';

export interface PendingTopUp {
  reference: string;
  amountMinor: number;
  /** Carried along so the follow-up message can format the amount
   *  even if the wallet summary itself fails to load. */
  currency: string;
  /** ISO 8601. Used to age the breadcrumb out. */
  startedAt: string;
}

/**
 * After this long we stop offering to check. A gateway session does not stay
 * open for an hour, and by then the webhook has long since settled the wallet.
 */
export const PENDING_TOP_UP_TTL_MS = 60 * 60 * 1000;

export function rememberPendingTopUp(pending: PendingTopUp): void {
  try {
    sessionStorage.setItem(KEY, JSON.stringify(pending));
  } catch {
    // Storage can be unavailable (private mode, a locked-down browser). Losing
    // the breadcrumb degrades the follow-up message; it must never stop the
    // agent from reaching the gateway, so this is swallowed on purpose.
  }
}

export function readPendingTopUp(now = Date.now()): PendingTopUp | null {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return null;

    const pending = JSON.parse(raw) as PendingTopUp;
    if (now - Date.parse(pending.startedAt) > PENDING_TOP_UP_TTL_MS) {
      sessionStorage.removeItem(KEY);
      return null;
    }

    return pending;
  } catch {
    // Unreadable or corrupt. Treat it as absent rather than crashing the page.
    return null;
  }
}

export function clearPendingTopUp(): void {
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    // See rememberPendingTopUp.
  }
}
