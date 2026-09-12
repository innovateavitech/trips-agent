import type { Invoice, InvoiceStatus, MyPlan, Plan, SubscriptionStatus } from './types';

/** A status, in words, with what it means — never just a coloured label. */
export interface StatusDisplay {
  label: string;
  tone: 'neutral' | 'primary' | 'success' | 'warning' | 'destructive' | 'info';
  meaning: string;
}

export function planStatusDisplay(status: SubscriptionStatus | null): StatusDisplay {
  switch (status) {
    case 'Active':
      return { label: 'Active', tone: 'success', meaning: 'Paid up to the end of this period.' };
    case 'Trialing':
      return {
        label: 'Free trial',
        tone: 'info',
        meaning: 'Nothing has been charged yet. Your first invoice comes when the trial ends.',
      };
    case 'PastDue':
      return {
        label: 'Payment failed',
        tone: 'warning',
        meaning:
          'Everything still works. We will try the card again, and nothing changes about your ' +
          'account while we do.',
      };
    case 'Cancelled':
      return { label: 'Cancelled', tone: 'destructive', meaning: 'Your plan has ended.' };
    case 'Expired':
      return { label: 'Expired', tone: 'neutral', meaning: 'Your trial ended without a payment.' };
    default:
      return {
        label: 'No plan',
        tone: 'neutral',
        meaning: 'Choose a plan to unlock sub-agents, a custom domain and more listings.',
      };
  }
}

export function invoiceStatusDisplay(status: InvoiceStatus): StatusDisplay {
  switch (status) {
    case 'Paid':
      return { label: 'Paid', tone: 'success', meaning: 'Settled. Your receipt number is on it.' };
    case 'PastDue':
      return {
        label: 'Unpaid',
        tone: 'warning',
        meaning: 'A charge did not go through. You can pay it with another card.',
      };
    case 'Uncollectible':
      return {
        label: 'Written off',
        tone: 'destructive',
        meaning: 'We stopped trying to collect it. Paying it puts your plan back.',
      };
    case 'Void':
      return { label: 'Cancelled', tone: 'neutral', meaning: 'Superseded before anyone paid it.' };
    default:
      return { label: 'Due', tone: 'info', meaning: 'Raised and waiting to be paid.' };
  }
}

/**
 * What the agency most needs to be told, if anything.
 *
 * At most one banner. Three warnings stacked on a screen is three warnings nobody reads, so this
 * picks the one that matters most and says what to do about it.
 */
export function urgentNotice(
  plan: MyPlan,
): { tone: 'warning' | 'destructive' | 'info'; title: string; detail: string } | null {
  if (plan.status === 'PastDue') {
    const left = Math.max(0, 4 - plan.dunningRetries);

    return {
      tone: left <= 1 ? 'destructive' : 'warning',
      title: 'We could not take your last payment',
      detail:
        left === 0
          ? 'We have tried every time we are going to. Pay the outstanding invoice below to put ' +
            'your plan back — nothing you have built has been removed.'
          : `Nothing has changed about your account. We will try again ${left === 1 ? 'once more' : `${left} more times`}, ` +
            'or you can pay the outstanding invoice below with another card.',
    };
  }

  if (plan.status === 'Trialing' && plan.cardOnFile === null) {
    return {
      tone: 'info',
      title: 'Your trial has no card behind it yet',
      detail:
        'When the trial ends we will raise your first invoice. Pay it and the card you use is the ' +
        'one we charge from then on.',
    };
  }

  if (plan.status === 'Active' && plan.amountMinor !== null && plan.cardOnFile === null) {
    return {
      tone: 'warning',
      title: 'There is no card on file',
      detail: 'Your next renewal cannot be charged. Pay your next invoice to put a card on file.',
    };
  }

  return null;
}

/**
 * Is choosing `plan` an upgrade, a downgrade, or neither?
 *
 * It decides what the button says and what the confirmation warns about, so it is worth being
 * explicit: a downgrade does not take effect today, and saying "switch now" would be a lie.
 */
export function planChangeKind(plan: Plan, current: MyPlan): 'current' | 'upgrade' | 'downgrade' {
  if (plan.isCurrent) return 'current';

  const now = current.amountMinor ?? 0;
  const next = plan.amountMinor ?? 0;

  return next > now ? 'upgrade' : 'downgrade';
}

/** What the button on a plan card says. */
export function planActionLabel(kind: ReturnType<typeof planChangeKind>): string {
  if (kind === 'current') return 'Your plan';
  return kind === 'upgrade' ? 'Upgrade' : 'Switch to this';
}

/** What the agency is agreeing to, spelled out before they agree to it. */
export function planChangeConsequences(
  plan: Plan,
  kind: ReturnType<typeof planChangeKind>,
): string {
  if (kind === 'upgrade') {
    return plan.trialDays > 0
      ? `You start a ${plan.trialDays}-day free trial. Nothing is charged until it ends.`
      : 'You pay for the new plan now, on a fresh period, and the new limits apply straight away.';
  }

  return (
    'It takes effect at the end of the period you have already paid for — you keep what you are ' +
    'paying for until then. Nothing you have already set up is removed. If the new plan allows ' +
    'fewer of something than you are using, you keep what you have and cannot add more until you ' +
    'are back inside the new limit.'
  );
}

/**
 * True when an invoice's lines add up to its total.
 *
 * The server and the database both guarantee this, so a false here means something is wrong with
 * what reached the screen rather than with the invoice. The detail view says so rather than
 * quietly drawing a total nobody can check.
 */
export function invoiceAddsUp(invoice: Invoice): boolean {
  return invoice.lines.reduce((total, line) => total + line.amountMinor, 0) === invoice.totalMinor;
}

/** The unpaid invoices, oldest first — what the "pay this" prompt points at. */
export function outstanding(invoices: Invoice[]): Invoice[] {
  return invoices
    .filter(
      (invoice) =>
        invoice.status === 'Open' ||
        invoice.status === 'PastDue' ||
        invoice.status === 'Uncollectible',
    )
    .sort((left, right) => left.issuedAt.localeCompare(right.issuedAt));
}
