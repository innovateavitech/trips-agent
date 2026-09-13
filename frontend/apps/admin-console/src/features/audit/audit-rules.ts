import type { AuditLogEntry } from './types';

export type Tone = 'neutral' | 'success' | 'warning' | 'destructive' | 'info';

/**
 * An action name as a person would say it.
 *
 * A mapping rather than a rule that un-snake-cases the string, because the wording is ours and
 * some of these read badly spaced out — `agency.profile_updated` is "Profile edited", not "Agency
 * profile updated", which is what the entity column already says.
 *
 * Anything not listed falls back to the raw name. That is deliberate: a new action should look
 * unfamiliar in the viewer rather than be quietly mislabelled by a guess.
 */
const ACTION_LABELS: Record<string, string> = {
  created: 'Created',
  updated: 'Edited',
  deleted: 'Deleted',
  'agency.profile_updated': 'Profile edited',
  'agency.verified': 'Verified',
  'agency.suspended': 'Suspended',
  'agency.reinstated': 'Reinstated',
  'agency.terminated': 'Terminated',
  'agency.exported': 'Data exported',
  'platform_user.created': 'Back-office account created',
  'platform_user.role_changed': 'Back-office role changed',
  'platform_user.status_changed': 'Back-office account status changed',
};

export function actionLabel(action: string): string {
  return ACTION_LABELS[action] ?? action;
}

/**
 * How loudly to draw an action.
 *
 * Only the ones that end or interrupt a business relationship are destructive. If routine edits
 * were coloured too, the trail would be a wall of colour and the terminations would not stand out
 * — which is the one thing somebody scanning this page is looking for.
 */
const DESTRUCTIVE = new Set(['agency.suspended', 'agency.terminated', 'deleted']);
const NOTABLE = new Set(['agency.verified', 'agency.reinstated', 'created']);

export function actionTone(action: string): Tone {
  if (DESTRUCTIVE.has(action)) return 'destructive';
  if (NOTABLE.has(action)) return 'success';
  if (action === 'agency.exported') return 'warning';
  return 'neutral';
}

/** One field that changed, as the detail panel shows it. */
export interface FieldChange {
  field: string;
  before: string | null;
  after: string | null;
}

/**
 * What actually changed between the two recorded states.
 *
 * The API stores both sides as jsonb text and hands them over untouched, so the viewer does the
 * comparison. Showing the whole of both objects would bury one edited column in forty unchanged
 * ones; showing only the difference is what makes the row answerable.
 *
 * Never throws. The states come out of a database column, and an entry the console cannot parse
 * must still render its who, what and why — losing the whole row to one bad character would hide
 * exactly the entry somebody is most likely hunting for.
 */
export function changedFields(entry: AuditLogEntry): FieldChange[] {
  const before = parseState(entry.beforeState);
  const after = parseState(entry.afterState);

  if (!before && !after) return [];

  const fields = [...new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})])].sort();

  return fields
    .map((field) => ({
      field,
      before: render(before?.[field]),
      after: render(after?.[field]),
    }))
    .filter((change) => change.before !== change.after);
}

/** True when the two states could not be read at all, so the panel can say so rather than lie. */
export function stateIsUnreadable(entry: AuditLogEntry): boolean {
  const hasText = Boolean(entry.beforeState ?? entry.afterState);
  return hasText && !parseState(entry.beforeState) && !parseState(entry.afterState);
}

function parseState(text: string | null): Record<string, unknown> | null {
  if (!text) return null;

  try {
    const parsed: unknown = JSON.parse(text);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/** One value as text. Objects and arrays keep their JSON so nothing is silently flattened away. */
function render(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);

  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

/**
 * A column name as a person would say it. `vat_rate_basis_points` → "Vat rate basis points".
 *
 * Unlike the action labels, guessing is safe here: these are column names, they are already
 * close to English, and getting one slightly stiff is better than a mapping nobody maintains.
 */
export function fieldLabel(field: string): string {
  const spaced = field
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim()
    .toLowerCase();

  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/**
 * Whose hand this was, as a badge beside the name.
 *
 * The one distinction worth drawing on this screen: a Trips admin acting on an agency and that
 * agency's own staff acting on themselves are different events, and the name alone does not say
 * which. `actorName` already resolves System and Anonymous, so this labels the kind, not the
 * person.
 */
export function actorTypeDisplay(actorType: string): { label: string; tone: Tone } {
  switch (actorType) {
    case 'PlatformAdmin':
      return { label: 'Trips staff', tone: 'info' };
    case 'User':
      return { label: 'Agency staff', tone: 'neutral' };
    case 'System':
      return { label: 'Background job', tone: 'neutral' };
    case 'Anonymous':
      return { label: 'Not signed in', tone: 'warning' };
    default:
      // Unknown means no actor could be determined, which the domain calls a bug. Say so.
      return { label: 'Unattributed', tone: 'destructive' };
  }
}
