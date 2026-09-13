import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  EmptyState,
  ErrorState,
  Input,
  LoadingState,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Textarea,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { deadlineTone, DISPUTE_STATUS, statusCopy, timeLeft } from '../payout-rules';
import { useDispute, useDisputes, useSubmitEvidence, type Dispute } from '../payout-queries';

/**
 * ============================================================================
 *  Build plan F12 — chargebacks, and the evidence that answers them.
 * ============================================================================
 *
 * Sorted with the soonest deadline first, because that is the only order that
 * reflects what is urgent. A dispute nobody answers is lost by default.
 */
export function DisputesPage() {
  const disputes = useDisputes();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Disputes"
        description="When a customer asks their bank to reverse a payment, it appears here. Send evidence before the deadline, or the bank decides without hearing from you."
      />

      {disputes.isPending ? (
        <LoadingState size="page" label="Loading disputes" />
      ) : disputes.isError ? (
        <ErrorState {...describeError(disputes.error)} onRetry={() => void disputes.refetch()} />
      ) : disputes.data.length === 0 ? (
        <EmptyState title="No disputes" headingLevel={2}>
          If a customer disputes a payment with their bank, you will be emailed and it will appear
          here with a deadline for evidence.
        </EmptyState>
      ) : (
        <Card>
          <CardContent className="overflow-x-auto pt-6">
            <DisputeTable disputes={disputes.data} now={new Date()} />
          </CardContent>
        </Card>
      )}
    </div>
  );
}

export function DisputeTable({ disputes, now }: { disputes: Dispute[]; now: Date }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Payment</TableHead>
          <TableHead className="text-right">Amount</TableHead>
          <TableHead>Status</TableHead>
          <TableHead>Evidence deadline</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {disputes.map((dispute) => {
          const status = statusCopy(DISPUTE_STATUS, dispute.status);
          const open = dispute.status === 'Open';

          return (
            <TableRow key={dispute.id}>
              <TableCell>
                <Link
                  className="font-mono text-xs text-primary hover:underline"
                  to={`/disputes/${dispute.id}`}
                >
                  {dispute.paymentReference}
                </Link>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {formatMoney(dispute.amountMinor, dispute.currency)}
              </TableCell>
              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
              </TableCell>
              <TableCell>
                {open ? (
                  <Badge tone={deadlineTone(dispute.evidenceDueAt, now)}>
                    {timeLeft(dispute.evidenceDueAt, now)}
                  </Badge>
                ) : (
                  <span className="text-sm text-muted-foreground">
                    {new Date(dispute.evidenceDueAt).toLocaleDateString('en-NG')}
                  </span>
                )}
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

export function DisputeDetailPage() {
  const { disputeId = '' } = useParams();
  const dispute = useDispute(disputeId);

  if (dispute.isPending) {
    return <LoadingState size="page" label="Loading the dispute" />;
  }

  if (dispute.isError) {
    return <ErrorState {...describeError(dispute.error)} onRetry={() => void dispute.refetch()} />;
  }

  const data = dispute.data;
  const status = statusCopy(DISPUTE_STATUS, data.status);
  const now = new Date();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Dispute on ${data.paymentReference}`}
        description={
          <Link className="text-primary hover:underline" to="/disputes">
            Back to disputes
          </Link>
        }
      />

      <Card>
        <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
          <div>
            <CardDescription>Disputed amount</CardDescription>
            <CardTitle className="text-3xl">
              {formatMoney(data.amountMinor, data.currency)}
            </CardTitle>
          </div>
          <Badge tone={status.tone}>{status.label}</Badge>
        </CardHeader>
        <CardContent className="flex flex-col gap-3 text-sm">
          <p>
            <span className="text-muted-foreground">Customer&apos;s reason: </span>
            {data.reason ?? data.category ?? 'Not given'}
          </p>
          <p>
            <span className="text-muted-foreground">Evidence deadline: </span>
            {new Date(data.evidenceDueAt).toLocaleString('en-NG', {
              dateStyle: 'medium',
              timeStyle: 'short',
            })}{' '}
            {data.status === 'Open' ? (
              <Badge tone={deadlineTone(data.evidenceDueAt, now)}>
                {timeLeft(data.evidenceDueAt, now)}
              </Badge>
            ) : null}
          </p>
          {data.holdOutcome === 'Held' ? (
            <Alert tone="info">
              We have held {formatMoney(data.amountMinor, data.currency)} from your wallet until the
              bank decides. It comes back if the dispute is decided in your favour.
            </Alert>
          ) : null}
          {data.holdOutcome === 'Uncovered' ? (
            <Alert tone="warning">
              Your wallet could not cover this amount. If the dispute is lost, it will be taken from
              your wallet as funds arrive.
            </Alert>
          ) : null}
        </CardContent>
      </Card>

      {data.acceptsEvidence ? (
        <EvidenceForm dispute={data} />
      ) : data.evidenceSubmittedAt ? (
        <Alert tone="success">
          Evidence was sent on {new Date(data.evidenceSubmittedAt).toLocaleDateString('en-NG')}. The
          bank will decide, and you will be emailed the outcome.
        </Alert>
      ) : null}
    </div>
  );
}

function EvidenceForm({ dispute }: { dispute: Dispute }) {
  const submit = useSubmitEvidence(dispute.id);
  const defaults = dispute.evidenceDefaults;

  const [customerName, setCustomerName] = useState(defaults?.customerName ?? '');
  const [customerEmail, setCustomerEmail] = useState(defaults?.customerEmail ?? '');
  const [customerPhone, setCustomerPhone] = useState(defaults?.customerPhone ?? '');
  const [serviceDetails, setServiceDetails] = useState(defaults?.serviceDetails ?? '');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  function onSubmit(event: FormEvent) {
    event.preventDefault();

    if (
      !customerName.trim() ||
      !customerEmail.trim() ||
      !customerPhone.trim() ||
      !serviceDetails.trim()
    ) {
      setError('Fill in the customer’s name, email and phone, and describe what was sold.');
      return;
    }

    setError(null);
    submit.mutate(
      {
        customerName,
        customerEmail,
        customerPhone,
        serviceDetails,
        deliveryDate: null,
        note,
        assetIds: null,
      },
      {
        onSuccess: () => setSent(true),
        onError: (failure) => setError(describeError(failure).detail),
      },
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Send evidence</CardTitle>
        <CardDescription>
          We filled in what we know from the booking. Check it, add anything that shows the customer
          received what they paid for, and send it. You can send it again before the deadline.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {sent ? (
          <Alert tone="success" className="mb-4">
            Evidence sent. You can update it until the deadline.
          </Alert>
        ) : null}
        {error ? (
          <Alert tone="destructive" className="mb-4">
            {error}
          </Alert>
        ) : null}
        <form className="grid gap-4 md:grid-cols-3" onSubmit={onSubmit} noValidate>
          <Input
            label="Customer name"
            value={customerName}
            onChange={(e) => setCustomerName(e.target.value)}
          />
          <Input
            label="Customer email"
            type="email"
            value={customerEmail}
            onChange={(e) => setCustomerEmail(e.target.value)}
          />
          <Input
            label="Customer phone"
            value={customerPhone}
            onChange={(e) => setCustomerPhone(e.target.value)}
          />
          <div className="md:col-span-3">
            <Textarea
              label="What was sold and delivered"
              value={serviceDetails}
              onChange={(e) => setServiceDetails(e.target.value)}
            />
          </div>
          <div className="md:col-span-3">
            <Textarea
              label="Anything else (kept with the dispute)"
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
          </div>
          <div className="md:col-span-3">
            <Button type="submit" disabled={submit.isPending}>
              {submit.isPending ? 'Sending…' : 'Send evidence'}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
