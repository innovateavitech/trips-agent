import type { ErasurePreview, ErasureResult } from './types';

/** A reason shorter than this is not a reason. Matches ErasureRequest.MinimumReasonLength. */
export const MINIMUM_REASON = 10;

/**
 * Whether the button may be pressed at all.
 *
 * Four conditions, and the screen enforces every one of them before the server does: somebody has
 * been found, nothing stands in the way, a reason has been written, and the person running it has
 * ticked to say they know it cannot be undone. The server checks the same things — this only keeps
 * an operator from firing an irreversible request by leaning on the keyboard.
 */
export function canErase(
  person: ErasurePreview | null,
  reason: string,
  confirmed: boolean,
): boolean {
  if (!person || person.blockers.length > 0 || !confirmed) return false;

  return reason.trim().length >= MINIMUM_REASON;
}

/** "crm.customers (1), orders.order_travellers (2)" — counts, never what they held. */
export function describeChanges(result: ErasureResult): string {
  const changed = Object.entries(result.changed)
    .filter(([, count]) => count > 0)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([table, count]) => `${table} (${count})`);

  return changed.length === 0 ? 'nothing was left to change' : changed.join(', ');
}

/** How a recorded request reads in the list. A refusal is not a failure, but it is not a success either. */
export function statusTone(status: string): 'success' | 'warning' {
  return status === 'Completed' ? 'success' : 'warning';
}
