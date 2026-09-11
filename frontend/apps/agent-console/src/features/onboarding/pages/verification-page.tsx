import { useState } from 'react';
import { ShieldCheck } from 'lucide-react';
import { Button, Card, ErrorState, LoadingState } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { DocumentCard } from '../components/document-card';
import { DocumentDropZone } from '../components/document-drop-zone';
import { ReviewBanner } from '../components/review-banner';
import { useKybStatus, useSubmitKyb, useUploadDocument, useUploadLimits } from '../kyb-queries';
import type { KybDocument, KybStatus, UploadLimits } from '../types';
import { describeDocumentType } from '../upload-rules';

/**
 * ============================================================================
 *  FRD §2.2 — business verification (KYB).
 * ============================================================================
 *
 * One screen for every state a submission can be in: nothing uploaded, partly
 * uploaded, waiting for review, rejected with a reason, and approved.
 *
 * Each file is sent the moment it is chosen, rather than held until a "save"
 * at the end. That is what makes a refresh — or a dropped connection halfway
 * through, which is the normal case on mobile data — cost nothing: whatever
 * arrived is on the server, and this screen reads its progress back from there.
 */
export function VerificationPage() {
  const status = useKybStatus();
  const limits = useUploadLimits();

  if (status.isPending || limits.isPending) {
    return <LoadingState size="page" label="Loading your verification" />;
  }

  if (status.isError || limits.isError) {
    const problem = describeError(status.error ?? limits.error);

    return (
      <ErrorState
        title={problem.title}
        detail={problem.detail}
        onRetry={() => {
          void status.refetch();
          void limits.refetch();
        }}
        retrying={status.isFetching || limits.isFetching}
      />
    );
  }

  return <Verification status={status.data} limits={limits.data} />;
}

function Verification({ status, limits }: { status: KybStatus; limits: UploadLimits }) {
  const upload = useUploadDocument();
  const submit = useSubmitKyb();

  // Which documents the agent has asked to replace. Server state says a document
  // exists; this says "show me the upload control for it anyway".
  const [replacing, setReplacing] = useState<string[]>([]);

  const uploaded = new Map(status.documents.map((d) => [d.documentType, d]));
  const required = limits.requiredDocumentTypes;
  const complete = status.missingDocumentTypes.length === 0;
  const isWaiting = status.status === 'Submitted' || status.status === 'UnderReview';

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Business verification
        </h1>
        <p className="text-sm text-muted-foreground">
          Trips verifies every agency before it can take payments or issue tickets.
        </p>
      </header>

      <ReviewBanner status={status} />

      <Card className="flex flex-col gap-5 p-6">
        <div className="flex flex-col gap-1">
          <h2 className="text-lg font-semibold text-foreground">Documents</h2>
          <p className="text-sm text-muted-foreground">
            {status.canEdit
              ? `${required.length - status.missingDocumentTypes.length} of ${required.length} attached. Each file is saved as soon as you choose it.`
              : 'These are the documents Trips is reviewing.'}
          </p>
        </div>

        <ul className="flex flex-col gap-5">
          {required.map((documentType) => (
            <DocumentRow
              key={documentType}
              documentType={documentType}
              document={uploaded.get(documentType)}
              limits={limits}
              canEdit={status.canEdit}
              isReplacing={replacing.includes(documentType)}
              isUploading={upload.isPending && upload.variables?.documentType === documentType}
              onReplace={() => setReplacing((types) => [...types, documentType])}
              onFile={(file) => {
                upload.mutate(
                  { documentType, file },
                  {
                    onSuccess: () =>
                      setReplacing((types) => types.filter((t) => t !== documentType)),
                  },
                );
              }}
            />
          ))}
        </ul>

        {upload.isError && (
          <ErrorState
            title={describeError(upload.error).title}
            detail={describeError(upload.error).detail}
          />
        )}
      </Card>

      {status.canEdit && (
        <Card className="flex flex-wrap items-center justify-between gap-4 p-6">
          <div className="flex flex-col gap-1">
            <p className="text-sm font-medium text-foreground">
              {complete ? 'Ready to send for review' : 'Attach every document to continue'}
            </p>
            <p className="text-sm text-muted-foreground">
              {complete
                ? 'You will not be able to change the documents while Trips is reviewing them.'
                : `Still needed: ${status.missingDocumentTypes
                    .map((t) => describeDocumentType(t).label.toLowerCase())
                    .join(', ')}.`}
            </p>
          </div>

          <Button
            onClick={() => submit.mutate()}
            disabled={!complete || submit.isPending}
            aria-disabled={!complete || submit.isPending}
          >
            <ShieldCheck className="size-4" aria-hidden="true" />
            {submit.isPending ? 'Sending…' : 'Submit for review'}
          </Button>
        </Card>
      )}

      {submit.isError && (
        <ErrorState
          title={describeError(submit.error).title}
          detail={describeError(submit.error).detail}
        />
      )}

      {isWaiting && (
        <p className="text-sm text-muted-foreground">
          Submitted{' '}
          {status.submittedAt !== null ? new Date(status.submittedAt).toLocaleString() : ''}. This
          page checks for an answer every few seconds.
        </p>
      )}
    </div>
  );
}

interface DocumentRowProps {
  documentType: string;
  document: KybDocument | undefined;
  limits: UploadLimits;
  canEdit: boolean;
  isReplacing: boolean;
  isUploading: boolean;
  onReplace: () => void;
  onFile: (file: File) => void;
}

function DocumentRow({
  documentType,
  document,
  limits,
  canEdit,
  isReplacing,
  isUploading,
  onReplace,
  onFile,
}: DocumentRowProps) {
  const { label, hint } = describeDocumentType(documentType);
  const showUpload = canEdit && (document === undefined || isReplacing);

  return (
    <li className="flex flex-col gap-2">
      <div className="flex flex-col gap-0.5">
        <span className="text-sm font-medium text-foreground">{label}</span>
        {hint.length > 0 && <span className="text-xs text-muted-foreground">{hint}</span>}
      </div>

      {document !== undefined && (
        <DocumentCard
          document={document}
          canReplace={canEdit && !isReplacing}
          onReplace={onReplace}
        />
      )}

      {showUpload && (
        <DocumentDropZone
          documentType={documentType}
          limits={limits}
          isUploading={isUploading}
          onFile={onFile}
        />
      )}
    </li>
  );
}
