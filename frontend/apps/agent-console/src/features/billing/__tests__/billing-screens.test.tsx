// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InvoiceTable } from '../components/invoice-table';
import { PlanSummary } from '../components/plan-summary';
import type { Invoice, MyPlan } from '../types';

afterEach(cleanup);

function invoice(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 'i1',
    invoiceNumber: 'TRIPS-INV-2026-000104',
    receiptNumber: 'TRIPS-RCT-2026-000091',
    status: 'Paid',
    statusReason: null,
    currency: 'NGN',
    totalMinor: 2_500_000,
    issuedAt: '2026-09-01T04:00:00Z',
    dueAt: '2026-09-01T04:00:00Z',
    paidAt: '2026-09-01T04:00:12Z',
    periodStart: '2026-09-01T00:00:00Z',
    periodEnd: '2026-10-01T00:00:00Z',
    lines: [
      {
        description: 'Growth plan — 1 Sep 2026 to 30 Sep 2026',
        quantity: 1,
        unitAmountMinor: 2_500_000,
        amountMinor: 2_500_000,
      },
    ],
    ...overrides,
  };
}

function myPlan(overrides: Partial<MyPlan> = {}): MyPlan {
  return {
    subscriptionId: 's1',
    tierId: 't1',
    planName: 'Growth',
    status: 'Active',
    statusReason: null,
    currency: 'NGN',
    amountMinor: 2_500_000,
    interval: 'Monthly',
    currentPeriodStart: '2026-09-01T00:00:00Z',
    currentPeriodEnd: '2026-10-01T00:00:00Z',
    trialEndsAt: null,
    nextChargeAt: '2026-10-01T00:00:00Z',
    cardOnFile: 'Visa •••• 4242',
    dunningRetries: 0,
    nextDunningAttemptAt: null,
    features: [
      { code: 'max_sub_agents', name: 'Sub-agents', display: '5' },
      { code: 'custom_domain', name: 'Custom domain', display: 'on' },
    ],
    scheduledChange: null,
    ...overrides,
  };
}

describe('InvoiceTable', () => {
  it('shows the invoice number, the receipt number and the total in naira', () => {
    render(<InvoiceTable invoices={[invoice()]} />);

    expect(screen.getByText('TRIPS-INV-2026-000104')).toBeTruthy();
    expect(screen.getByText(/TRIPS-RCT-2026-000091/)).toBeTruthy();

    // ₦25,000.00 — two and a half million kobo, divided by 100 at the last moment.
    expect(screen.getAllByText(/25,000\.00/).length).toBeGreaterThan(0);
  });

  it('keeps the lines hidden until they are asked for, then shows them adding up', () => {
    render(<InvoiceTable invoices={[invoice()]} />);

    expect(screen.queryByText(/Growth plan — 1 Sep 2026/)).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Show lines' }));

    expect(screen.getByText(/Growth plan — 1 Sep 2026/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Hide lines' })).toBeTruthy();

    // Two now: the column header, and the expanded panel's own total under the lines.
    expect(screen.getAllByText('Total')).toHaveLength(2);
  });

  it('says so when a total does not match its own lines rather than drawing it quietly', () => {
    render(<InvoiceTable invoices={[invoice({ totalMinor: 9_999 })]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Show lines' }));

    expect(screen.getByText(/do not add up to the total shown/)).toBeTruthy();
  });

  it('shows a quantity on a line that has more than one of something', () => {
    const many = invoice({
      totalMinor: 3_000_000,
      lines: [
        {
          description: 'Extra seats',
          quantity: 3,
          unitAmountMinor: 1_000_000,
          amountMinor: 3_000_000,
        },
      ],
    });

    render(<InvoiceTable invoices={[many]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Show lines' }));

    expect(screen.getByText(/Extra seats × 3/)).toBeTruthy();
  });
});

describe('PlanSummary', () => {
  it('names the plan, its price and the card it is charged to', () => {
    render(
      <PlanSummary
        plan={myPlan()}
        onChangePlan={vi.fn()}
        onCancelScheduled={vi.fn()}
        cancelling={false}
      />,
    );

    expect(screen.getByRole('heading', { name: 'Growth' })).toBeTruthy();
    expect(screen.getByText(/25,000\.00/)).toBeTruthy();
    expect(screen.getByText('Visa •••• 4242')).toBeTruthy();
  });

  it('says there is no card rather than leaving the field blank', () => {
    render(
      <PlanSummary
        plan={myPlan({ cardOnFile: null })}
        onChangePlan={vi.fn()}
        onCancelScheduled={vi.fn()}
        cancelling={false}
      />,
    );

    expect(screen.getByText('None yet')).toBeTruthy();
  });

  it('offers "choose a plan" to an agency that has none', () => {
    render(
      <PlanSummary
        plan={myPlan({
          subscriptionId: null,
          status: null,
          amountMinor: null,
          planName: 'No plan',
        })}
        onChangePlan={vi.fn()}
        onCancelScheduled={vi.fn()}
        cancelling={false}
      />,
    );

    expect(screen.getByRole('button', { name: 'Choose a plan' })).toBeTruthy();
    expect(screen.getByText('Free')).toBeTruthy();
  });

  it('shows a scheduled change with the date it lands and a way to call it off', () => {
    const onCancel = vi.fn();

    render(
      <PlanSummary
        plan={myPlan({
          scheduledChange: {
            migrationId: 'm1',
            fromPlan: 'Growth',
            toPlan: 'Starter',
            reason: 'Downgrade',
            explanation: 'You asked to move to the Starter plan.',
            effectiveAt: '2026-10-01T00:00:00Z',
            canCancel: true,
          },
        })}
        onChangePlan={vi.fn()}
        onCancelScheduled={onCancel}
        cancelling={false}
      />,
    );

    expect(screen.getByText(/Your plan changes to Starter on 1 October 2026/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Keep my current plan instead' }));

    expect(onCancel).toHaveBeenCalledWith('m1');
  });

  /**
   * A change Trips made — an admin migrating a tier's subscribers, or a fallback after dunning —
   * is not the agency's to cancel here. Offering the button and then refusing would be worse than
   * not offering it.
   */
  it('does not offer to cancel a change the agency did not ask for', () => {
    render(
      <PlanSummary
        plan={myPlan({
          scheduledChange: {
            migrationId: 'm2',
            fromPlan: 'Legacy',
            toPlan: 'Growth',
            reason: 'AdminMigration',
            explanation: 'Trips is retiring the Legacy plan.',
            effectiveAt: '2026-10-15T00:00:00Z',
            canCancel: false,
          },
        })}
        onChangePlan={vi.fn()}
        onCancelScheduled={vi.fn()}
        cancelling={false}
      />,
    );

    expect(screen.getByText(/Trips is retiring the Legacy plan/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Keep my current plan instead' })).toBeNull();
  });

  it('shows the trial end date instead of a next charge while trialing', () => {
    render(
      <PlanSummary
        plan={myPlan({
          status: 'Trialing',
          trialEndsAt: '2026-09-15T00:00:00Z',
          nextChargeAt: null,
        })}
        onChangePlan={vi.fn()}
        onCancelScheduled={vi.fn()}
        cancelling={false}
      />,
    );

    const facts = screen.getByRole('heading', { name: 'Growth' }).closest('div')?.parentElement
      ?.parentElement?.parentElement;

    expect(screen.getByText('Trial ends')).toBeTruthy();
    expect(within(facts as HTMLElement).getByText('15 September 2026')).toBeTruthy();
  });
});
