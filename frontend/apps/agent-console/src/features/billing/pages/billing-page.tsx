import { useState } from 'react';
import { Alert, Card, EmptyState, ErrorState } from '@trips/ui';
import { PageHeader } from '../../../shell/page-header';
import { InvoiceTable, InvoiceTableSkeleton } from '../components/invoice-table';
import { PlanPicker } from '../components/plan-picker';
import { PlanSummary, PlanSummarySkeleton } from '../components/plan-summary';
import { outstanding, urgentNotice } from '../billing-rules';
import {
  useCancelScheduledChange,
  useChoosePlan,
  useInvoices,
  useMyPlan,
  usePlans,
} from '../billing-queries';

/**
 * Billing: what this agency pays Trips, and what it gets for it.
 *
 * One screen rather than three, because the three questions arrive together — what am I on, what
 * have I been charged, and can I change it. Splitting them would mean answering "why was I charged
 * this?" on a page that does not say what the plan is.
 *
 * Card entry happens on Paystack's page and nowhere else. When a change needs paying for, this
 * screen sends the agency there; it never asks for a card number, and there is no field here that
 * could.
 */
export function BillingPage() {
  const [picking, setPicking] = useState(false);

  const plan = useMyPlan();
  const plans = usePlans();
  const invoices = useInvoices();

  const choose = useChoosePlan();
  const cancel = useCancelScheduledChange();

  const notice = plan.data ? urgentNotice(plan.data) : null;
  const unpaid = invoices.data ? outstanding(invoices.data) : [];

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Billing"
        description="Your plan with Trips, what it includes, and everything we have charged you."
      />

      {notice ? (
        <Alert tone={notice.tone} title={notice.title}>
          {notice.detail}
        </Alert>
      ) : null}

      {plan.isError ? (
        <ErrorState
          title="We could not load your plan"
          detail="Your account is unaffected — this is a problem reading it, not a problem with your plan."
          onRetry={() => void plan.refetch()}
        />
      ) : null}

      {plan.isPending ? <PlanSummarySkeleton /> : null}

      {plan.data ? (
        <PlanSummary
          plan={plan.data}
          cancelling={cancel.isPending}
          onChangePlan={() => {
            choose.reset();
            setPicking(true);
          }}
          onCancelScheduled={(migrationId) => cancel.mutate(migrationId)}
        />
      ) : null}

      {unpaid.length > 0 ? (
        <Alert
          tone="warning"
          title={`${unpaid.length === 1 ? 'One invoice is' : `${unpaid.length} invoices are`} unpaid`}
        >
          Paying puts everything back as it was. Nothing you have already built has been removed.
        </Alert>
      ) : null}

      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold tracking-tight text-foreground">
          Invoices and receipts
        </h2>

        {invoices.isError ? (
          <ErrorState
            title="We could not load your invoices"
            detail="Nothing has changed about what you owe — this is a problem reading it."
            onRetry={() => void invoices.refetch()}
          />
        ) : null}

        <Card className="overflow-hidden">
          {invoices.isPending ? <InvoiceTableSkeleton /> : null}

          {invoices.data && invoices.data.length === 0 ? (
            <EmptyState title="Nothing has been charged yet">
              Your invoices appear here as soon as your plan is billed. Each one shows the lines it
              is made of, and becomes your receipt once it is paid.
            </EmptyState>
          ) : null}

          {invoices.data && invoices.data.length > 0 ? (
            <InvoiceTable invoices={invoices.data} />
          ) : null}
        </Card>
      </section>

      {plan.data ? (
        <PlanPicker
          open={picking}
          onOpenChange={setPicking}
          plans={plans.data ?? []}
          current={plan.data}
          pending={choose.isPending}
          error={choose.error}
          onChoose={(chosen) =>
            choose.mutate(
              {
                tierId: chosen.tierId,
                // Back to this screen with the gateway's reference, which the return page hands to
                // the server to ask what actually happened.
                callbackUrl: `${window.location.origin}/billing/return`,
              },
              {
                onSuccess: (result) => {
                  if (result.result === 'payment-required') {
                    // Paystack's hosted page. The only place a card is ever typed.
                    window.location.assign(result.authorizationUrl);
                    return;
                  }

                  setPicking(false);
                },
              },
            )
          }
        />
      ) : null}
    </div>
  );
}
