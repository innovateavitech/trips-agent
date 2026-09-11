import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Badge, Card, Skeleton, buttonVariants } from '@trips/ui';
import { BackLink, Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { ApiError, describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle, useNow } from '../../../lib/hooks';
import { AgencyFacts } from '../components/agency-facts';
import { DecisionPanel } from '../components/decision-panel';
import { DocumentList, DocumentViewer } from '../components/documents';
import { isAwaitingDecision } from '../decision-rules';
import { useKybQueue, useKybSubmission } from '../kyb-review-queries';
import { nextInQueue, waitingTime } from '../queue-rules';
import { agencyDisplayName, agencyStatusDisplay, submissionStatusDisplay } from '../status-display';
import type { KybReviewDetail } from '../types';

/** One submission: the agency's details, its documents, and the decision. */
export function KybSubmissionPage() {
  const { submissionId = '' } = useParams();
  const submission = useKybSubmission(submissionId);
  const detail = submission.data;

  useDocumentTitle(detail ? agencyDisplayName(detail) : 'KYB submission');

  return (
    <Page wide>
      <BackLink to="/kyb">KYB queue</BackLink>

      {submission.isPending ? <SubmissionSkeleton /> : null}

      {!detail && submission.isError ? (
        submission.error instanceof ApiError && submission.error.status === 404 ? (
          <Card>
            <EmptyState
              title="There is no submission at this address"
              action={
                <Link to="/kyb" className={buttonVariants({ variant: 'outline' })}>
                  Back to the queue
                </Link>
              }
            >
              It may have been removed, or the link may be wrong.
            </EmptyState>
          </Card>
        ) : (
          <ErrorState
            {...describeLoadError(submission.error)}
            onRetry={() => void submission.refetch()}
            retrying={submission.isFetching}
          />
        )
      ) : null}

      {/* Keyed by submission: every piece of local state here — the chosen document, the draft
          rejection reason, the result banner — belongs to one agency and must never survive into
          the next agency's page. */}
      {detail ? <SubmissionView key={detail.submissionId} detail={detail} /> : null}
    </Page>
  );
}

function SubmissionView({ detail }: { detail: KybReviewDetail }) {
  const queue = useKybQueue();
  const now = useNow();

  const [selectedId, setSelectedId] = useState<string | null>(
    () => detail.documents[0]?.id ?? null,
  );
  const [viewedIds, setViewedIds] = useState<ReadonlySet<string>>(
    () => new Set(detail.documents[0] ? [detail.documents[0].id] : []),
  );

  const name = agencyDisplayName(detail);
  const agencyStatus = agencyStatusDisplay(detail.agencyStatus);
  const submissionStatus = submissionStatusDisplay(detail.submissionStatus);
  const waiting = waitingTime(detail.submittedAt, now);
  const selected = detail.documents.find((document) => document.id === selectedId) ?? null;

  const next = useMemo(
    () => (queue.data ? nextInQueue(queue.data, detail.submissionId) : null),
    [queue.data, detail.submissionId],
  );

  function select(documentId: string) {
    setSelectedId(documentId);
    setViewedIds((seen) => (seen.has(documentId) ? seen : new Set(seen).add(documentId)));
  }

  const differentLegalName =
    detail.tradingName !== null && detail.tradingName.trim() !== detail.legalName;

  return (
    <>
      <PageHeader
        title={name}
        description={differentLegalName ? `Registered as ${detail.legalName}` : undefined}
      >
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={agencyStatus.tone}>{agencyStatus.label}</Badge>
          <Badge tone={submissionStatus.tone}>{submissionStatus.label}</Badge>
          {isAwaitingDecision(detail.submissionStatus) ? (
            <span className="text-sm text-muted-foreground">
              Waiting {waiting.label.toLowerCase()}
            </span>
          ) : null}
        </div>
      </PageHeader>

      <div className="grid gap-6 lg:grid-cols-5 lg:items-start">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <AgencyFacts detail={detail} />
          <DocumentList
            documents={detail.documents}
            selectedId={selectedId}
            viewedIds={viewedIds}
            onSelect={select}
          />
          <DecisionPanel
            detail={detail}
            next={next}
            unopenedCount={detail.documents.length - viewedIds.size}
          />
        </div>

        <div className="lg:sticky lg:top-6 lg:col-span-3">
          <DocumentViewer key={selected?.id ?? 'none'} document={selected} agencyName={name} />
        </div>
      </div>
    </>
  );
}

function SubmissionSkeleton() {
  return (
    <div aria-busy="true" aria-label="Loading the submission" className="flex flex-col gap-6">
      <div className="flex flex-col gap-2">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-5 w-40" />
      </div>
      <div className="grid gap-6 lg:grid-cols-5 lg:items-start">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <Skeleton className="h-64 w-full" />
          <Skeleton className="h-40 w-full" />
        </div>
        <Skeleton className="aspect-square w-full lg:col-span-3" />
      </div>
    </div>
  );
}
