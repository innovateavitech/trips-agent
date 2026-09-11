import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Textarea,
  buttonVariants,
} from '@trips/ui';
import { CheckIcon } from '../../../components/icons';
import {
  REJECTION_REASON_MAX,
  describeDecisionFailure,
  isAwaitingDecision,
  validateRejectionReason,
  type DecisionFailure,
} from '../decision-rules';
import {
  useApproveSubmission,
  useRefreshKybReview,
  useRejectSubmission,
} from '../kyb-review-queries';
import type { RankedQueueItem } from '../queue-rules';
import { agencyDisplayName, submissionStatusDisplay } from '../status-display';
import type { KybReviewDetail } from '../types';

/**
 * Approve, or reject with a reason — each behind a confirmation step.
 *
 * Why the confirmation is inline rather than a modal: the reviewer should still see the documents
 * while they confirm, and the reason they typed while they decide whether to send it.
 *
 * Why every decision has a confirmation at all: both are emailed to the agency the moment they are
 * saved, and there is no undo on this screen. One deliberate extra click is cheap.
 *
 * Mount it with `key={submissionId}`. Its local state — the draft reason, the result banner —
 * belongs to one agency, and must never still be on screen when the next agency's page opens.
 */
type Step = 'choose' | 'confirm-approve' | 'write-reason' | 'confirm-reject';

export function DecisionPanel({
  detail,
  next,
  unopenedCount,
}: {
  detail: KybReviewDetail;
  next: RankedQueueItem | null;
  /** Documents the reviewer has not opened yet. A nudge, not a block. */
  unopenedCount: number;
}) {
  const name = agencyDisplayName(detail);
  const approve = useApproveSubmission(detail.submissionId);
  const reject = useRejectSubmission(detail.submissionId);
  const refresh = useRefreshKybReview();

  const [step, setStep] = useState<Step>('choose');
  const [draft, setDraft] = useState('');
  const [reasonError, setReasonError] = useState<string>();
  const [outcome, setOutcome] = useState<'approved' | 'rejected' | null>(null);

  // Move focus to the confirmation as it appears, so a keyboard or screen-reader user is told what
  // they are confirming instead of being left on a button that no longer exists.
  const confirmHeadingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (step === 'confirm-approve' || step === 'confirm-reject') confirmHeadingRef.current?.focus();
  }, [step]);

  const error = approve.error ?? reject.error;
  const failure = error ? describeDecisionFailure(error) : null;
  const busy = approve.isPending || reject.isPending;

  function backToChoice() {
    approve.reset();
    reject.reset();
    setReasonError(undefined);
    setStep('choose');
  }

  function confirmApprove() {
    approve.mutate(undefined, { onSuccess: () => setOutcome('approved') });
  }

  function reviewReason(event: FormEvent) {
    event.preventDefault();
    const check = validateRejectionReason(draft);
    if (!check.ok) {
      setReasonError(check.error);
      return;
    }
    setReasonError(undefined);
    setDraft(check.reason);
    setStep('confirm-reject');
  }

  function sendRejection() {
    reject.mutate(draft, {
      onSuccess: () => setOutcome('rejected'),
      onError: (rejection) => {
        // The server refused the reason itself: put the reviewer back in the text box with the
        // server's message, rather than showing it in a banner far from the field.
        const described = describeDecisionFailure(rejection);
        if (described.kind === 'reason-invalid') {
          reject.reset();
          setReasonError(described.title);
          setStep('write-reason');
        }
      },
    });
  }

  if (outcome) return <DecisionResult outcome={outcome} name={name} next={next} />;

  if (!isAwaitingDecision(detail.submissionStatus)) {
    return <DecidedSummary detail={detail} failure={failure} />;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Decision</CardTitle>
        <CardDescription>
          {name} cannot fund its wallet or sell until it is approved.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-4">
        {failure && failure.kind !== 'reason-invalid' ? (
          <FailureAlert failure={failure} onRefresh={() => void refresh()} />
        ) : null}

        {step === 'choose' ? (
          <>
            {unopenedCount > 0 ? (
              <p className="text-sm text-muted-foreground">
                You have not opened{' '}
                {unopenedCount === 1 ? '1 document' : `${unopenedCount} documents`} yet.
              </p>
            ) : null}
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => setStep('confirm-approve')}>
                <CheckIcon />
                Approve
              </Button>
              <Button variant="outline" onClick={() => setStep('write-reason')}>
                Reject
              </Button>
            </div>
          </>
        ) : null}

        {step === 'confirm-approve' ? (
          <div className="flex animate-fade-in flex-col gap-3 rounded-md border border-primary-border bg-primary-subtle p-4">
            <h3
              ref={confirmHeadingRef}
              tabIndex={-1}
              className="text-sm font-semibold text-foreground focus-visible:outline-none"
            >
              Approve {name}?
            </h3>
            <p className="text-sm text-foreground">
              {name} is verified straight away. It can fund its wallet and start selling, and we
              email it the decision. This cannot be undone from here.
            </p>
            <div className="flex flex-wrap gap-2">
              <Button onClick={confirmApprove} loading={approve.isPending}>
                Confirm approval
              </Button>
              <Button variant="ghost" onClick={backToChoice} disabled={busy}>
                Go back
              </Button>
            </div>
          </div>
        ) : null}

        {step === 'write-reason' ? (
          <form onSubmit={reviewReason} noValidate className="flex animate-fade-in flex-col gap-3">
            <Textarea
              label="Reason for rejection"
              autoFocus
              rows={5}
              maxLength={REJECTION_REASON_MAX}
              value={draft}
              onChange={(event) => {
                setDraft(event.target.value);
                setReasonError(undefined);
              }}
              error={reasonError}
              hint={`${name} reads this word for word, in its email and on its onboarding screen. Say what to fix.`}
            />
            <p className="-mt-1 text-right text-xs tabular-nums text-muted-foreground">
              {draft.length} / {REJECTION_REASON_MAX}
            </p>
            <div className="flex flex-wrap gap-2">
              <Button type="submit" variant="outline">
                Continue
              </Button>
              <Button type="button" variant="ghost" onClick={backToChoice}>
                Cancel
              </Button>
            </div>
          </form>
        ) : null}

        {step === 'confirm-reject' ? (
          <div className="flex animate-fade-in flex-col gap-3 rounded-md border border-destructive-subtle bg-destructive-subtle p-4">
            <h3
              ref={confirmHeadingRef}
              tabIndex={-1}
              className="text-sm font-semibold text-destructive-subtle-foreground focus-visible:outline-none"
            >
              Reject {name}?
            </h3>
            <p className="text-sm text-destructive-subtle-foreground">
              We email {name} this reason, exactly as written:
            </p>
            <blockquote className="whitespace-pre-wrap break-words rounded-md border border-border bg-background p-3 text-sm text-foreground">
              {draft}
            </blockquote>
            <p className="text-sm text-destructive-subtle-foreground">
              They can upload corrected documents and submit again.
            </p>
            <div className="flex flex-wrap gap-2">
              <Button variant="destructive" onClick={sendRejection} loading={reject.isPending}>
                Send rejection
              </Button>
              <Button
                variant="ghost"
                onClick={() => {
                  reject.reset();
                  setStep('write-reason');
                }}
                disabled={busy}
              >
                Edit reason
              </Button>
            </div>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

function FailureAlert({ failure, onRefresh }: { failure: DecisionFailure; onRefresh: () => void }) {
  const uncertain = failure.kind === 'unknown-outcome' || failure.kind === 'already-decided';

  return (
    <Alert
      tone={uncertain ? 'warning' : 'destructive'}
      title={failure.title}
      action={
        uncertain ? (
          <Button size="sm" variant="outline" onClick={onRefresh}>
            Refresh submission
          </Button>
        ) : null
      }
    >
      {failure.detail}
    </Alert>
  );
}

function DecisionResult({
  outcome,
  name,
  next,
}: {
  outcome: 'approved' | 'rejected';
  name: string;
  next: RankedQueueItem | null;
}) {
  const headingRef = useRef<HTMLDivElement>(null);
  useEffect(() => headingRef.current?.focus(), []);

  return (
    <Card>
      <CardContent className="flex flex-col gap-4 p-5">
        <div ref={headingRef} tabIndex={-1} className="focus-visible:outline-none">
          <Alert
            tone="success"
            title={outcome === 'approved' ? `${name} is verified` : `Rejection sent to ${name}`}
          >
            {outcome === 'approved'
              ? 'They can fund their wallet and start selling now. We are emailing them the decision.'
              : 'We are emailing them your reason. They can upload corrected documents and submit again.'}
          </Alert>
        </div>

        <div className="flex flex-wrap gap-2">
          {next ? (
            <Link to={`/kyb/${next.submissionId}`} className={buttonVariants()}>
              Review next: {next.agencyName}
            </Link>
          ) : null}
          <Link to="/kyb" className={buttonVariants({ variant: next ? 'outline' : 'primary' })}>
            Back to the queue
          </Link>
        </div>
        {!next ? (
          <p className="text-sm text-muted-foreground">That was the last one waiting.</p>
        ) : null}
      </CardContent>
    </Card>
  );
}

/** Shown when the page opens on a submission that is no longer waiting, or was decided elsewhere. */
function DecidedSummary({
  detail,
  failure,
}: {
  detail: KybReviewDetail;
  failure: DecisionFailure | null;
}) {
  const status = submissionStatusDisplay(detail.submissionStatus);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Decision</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {failure?.kind === 'already-decided' ? (
          <Alert tone="warning" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <p className="text-sm text-foreground">
          {detail.submissionStatus === 'Draft'
            ? 'The agency has not submitted this yet, so there is nothing to decide.'
            : `This submission is ${status.label.toLowerCase()}. There is nothing left to decide.`}
        </p>

        {detail.submissionStatus === 'Rejected' && detail.rejectionReason ? (
          <div className="flex flex-col gap-1.5">
            <p className="text-xs text-muted-foreground">Reason sent to the agency</p>
            <blockquote className="whitespace-pre-wrap break-words rounded-md border border-border bg-muted p-3 text-sm text-foreground">
              {detail.rejectionReason}
            </blockquote>
          </div>
        ) : null}

        <Link to="/kyb" className={buttonVariants({ variant: 'outline', className: 'w-fit' })}>
          Back to the queue
        </Link>
      </CardContent>
    </Card>
  );
}
