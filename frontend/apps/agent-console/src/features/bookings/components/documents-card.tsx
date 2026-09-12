import { FileText } from 'lucide-react';
import { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  Skeleton,
  buttonVariants,
  cn,
} from '@trips/ui';
import {
  DOCUMENT_LABEL,
  describeEmail,
  documentHref,
  isReplaced,
  orderDocuments,
} from '../documents-rules';
import type { BookingDocument } from '../types';

export interface DocumentsCardProps {
  documents: BookingDocument[] | undefined;
  loading: boolean;
  /** Why the documents could not be loaded or reissued, in words, or `null`. */
  problem: string | null;
  /** Documents exist once a booking is ticketed; before then the card says so. */
  ticketed: boolean;
  /** The document being reissued right now, if any. */
  reissuingId: string | null;
  onReissue: (document: BookingDocument) => void;
}

/**
 * A booking's invoices and vouchers (#46): download each, and reissue one when
 * it needs correcting. A reissue is confirmed first, because it takes a new
 * number and emails the customer — and the card says plainly that the original
 * stays on record exactly as it was.
 */
export function DocumentsCard({
  documents,
  loading,
  problem,
  ticketed,
  reissuingId,
  onReissue,
}: DocumentsCardProps) {
  const [asking, setAsking] = useState<BookingDocument | null>(null);
  const ordered = orderDocuments(documents ?? []);
  const label = asking ? (DOCUMENT_LABEL[asking.documentType] ?? 'Document').toLowerCase() : '';

  return (
    <Card className="flex flex-col gap-3 p-5">
      <p className="text-sm font-medium text-foreground">Documents</p>

      {loading ? <Skeleton className="h-16 w-full" /> : null}

      {problem ? (
        <p role="alert" className="text-xs text-destructive">
          {problem}
        </p>
      ) : null}

      {!loading && ordered.length === 0 ? (
        <p className="text-xs text-muted-foreground">
          {ticketed
            ? 'Your invoice and voucher are being prepared.'
            : 'Available once the booking is ticketed.'}
        </p>
      ) : null}

      {ordered.length > 0 ? (
        <ul className="flex flex-col divide-y divide-border">
          {ordered.map((document) => (
            <DocumentRow
              key={document.id}
              document={document}
              reissuing={reissuingId === document.id}
              busy={reissuingId !== null}
              onReissue={() => setAsking(document)}
            />
          ))}
        </ul>
      ) : null}

      <p className="text-xs text-muted-foreground">
        In your own branding. Your customer is emailed a copy of each.
      </p>

      <Dialog open={asking !== null} onOpenChange={(open) => (open ? undefined : setAsking(null))}>
        {asking ? (
          <DialogContent>
            <DialogTitle>
              Reissue {label} {asking.documentNumber}?
            </DialogTitle>
            <DialogDescription>
              A new {label} with a new number replaces it, drawn from the booking as it is now, and
              your customer is emailed the new copy. {asking.documentNumber} stays on record exactly
              as it was issued — it cannot be changed.
            </DialogDescription>
            <DialogFooter>
              <Button variant="outline" onClick={() => setAsking(null)}>
                Not now
              </Button>
              <Button
                onClick={() => {
                  onReissue(asking);
                  setAsking(null);
                }}
              >
                Reissue
              </Button>
            </DialogFooter>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}

function DocumentRow({
  document,
  reissuing,
  busy,
  onReissue,
}: {
  document: BookingDocument;
  reissuing: boolean;
  busy: boolean;
  onReissue: () => void;
}) {
  const label = DOCUMENT_LABEL[document.documentType] ?? 'Document';
  const replaced = isReplaced(document);
  const email = describeEmail(document.email);
  const issue = Number(document.issueNumber);
  const name = `${label.toLowerCase()} ${document.documentNumber}`;

  return (
    <li className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p
          className={cn(
            'flex min-w-0 items-center gap-2 text-sm font-medium',
            replaced ? 'text-muted-foreground' : 'text-foreground',
          )}
        >
          <FileText aria-hidden="true" className="h-4 w-4 shrink-0 text-muted-foreground" />
          <span className="truncate">
            {label} {document.documentNumber}
          </span>
        </p>
        <div className="flex flex-wrap gap-1">
          {issue > 1 ? <Badge tone="neutral">Issue {issue}</Badge> : null}
          {document.status === 'Pending' ? <Badge tone="info">Being prepared</Badge> : null}
          {document.status === 'Failed' ? (
            <Badge tone="destructive">Could not be prepared</Badge>
          ) : null}
        </div>
      </div>

      {replaced ? (
        <p className="text-xs text-muted-foreground">
          Replaced by {document.supersededByDocumentNumber}. Kept on record, unchanged.
        </p>
      ) : null}

      {!replaced && document.supersedesDocumentNumber ? (
        <p className="text-xs text-muted-foreground">
          Replaces {document.supersedesDocumentNumber}.
        </p>
      ) : null}

      {email ? (
        <p
          className={cn(
            'text-xs',
            email.tone === 'destructive'
              ? 'text-destructive'
              : email.tone === 'success'
                ? 'text-success-subtle-foreground'
                : 'text-muted-foreground',
          )}
        >
          {email.text}
        </p>
      ) : null}

      {document.status === 'Ready' ? (
        <div className="flex flex-wrap gap-2">
          {document.downloadUrl ? (
            <a
              href={documentHref(document.downloadUrl)}
              download={document.fileName ?? true}
              target="_blank"
              rel="noreferrer"
              aria-label={`Download ${name}`}
              className={buttonVariants({ variant: 'outline', size: 'sm' })}
            >
              Download
            </a>
          ) : null}
          {!replaced ? (
            <Button
              variant="ghost"
              size="sm"
              loading={reissuing}
              disabled={busy}
              aria-label={`Reissue ${name}`}
              onClick={onReissue}
            >
              Reissue
            </Button>
          ) : null}
        </div>
      ) : null}
    </li>
  );
}
