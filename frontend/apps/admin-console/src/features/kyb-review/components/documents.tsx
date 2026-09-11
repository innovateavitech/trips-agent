import { useState } from 'react';
import { Card, CardDescription, CardHeader, CardTitle, buttonVariants, cn } from '@trips/ui';
import { EmptyState } from '../../../components/states';
import { CheckIcon, DocumentIcon, ExternalIcon } from '../../../components/icons';
import {
  documentTypeLabel,
  formatFileSize,
  isSafeDocumentUrl,
  previewKind,
} from '../document-display';
import type { KybReviewDocument } from '../types';

/**
 * The list of uploaded files. Choosing one shows it in the viewer; each also opens in a new tab
 * for a closer look. Files the reviewer has opened get a tick, so on a four-document submission
 * it is obvious which one has not been read yet.
 */
export function DocumentList({
  documents,
  selectedId,
  viewedIds,
  onSelect,
}: {
  documents: KybReviewDocument[];
  selectedId: string | null;
  viewedIds: ReadonlySet<string>;
  onSelect: (documentId: string) => void;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Documents</CardTitle>
        <CardDescription>
          {documents.length === 1 ? '1 file' : `${documents.length} files`}. Links are private and
          expire after 10 minutes; this page renews them while it is open.
        </CardDescription>
      </CardHeader>

      {documents.length === 0 ? (
        <EmptyState title="No documents are attached">
          This submission reached the queue without any files. Reject it and ask the agency to
          upload them.
        </EmptyState>
      ) : (
        <ul className="flex flex-col gap-1 px-3 pb-3">
          {documents.map((document) => {
            const selected = document.id === selectedId;
            const viewed = viewedIds.has(document.id);
            const label = documentTypeLabel(document.documentType);

            return (
              <li key={document.id} className="flex items-center gap-1">
                <button
                  type="button"
                  aria-pressed={selected}
                  onClick={() => onSelect(document.id)}
                  className={cn(
                    'flex min-w-0 flex-1 items-center gap-3 rounded-md px-2.5 py-2 text-left transition-colors',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                    selected ? 'bg-primary-subtle' : 'hover:bg-accent',
                  )}
                >
                  <DocumentIcon
                    className={cn(
                      'h-5 w-5 shrink-0',
                      selected ? 'text-primary' : 'text-muted-foreground',
                    )}
                  />
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="text-sm font-medium text-foreground">{label}</span>
                    <span className="truncate text-xs text-muted-foreground">
                      {document.fileName}
                    </span>
                  </span>
                  <span className="shrink-0 text-xs tabular-nums text-muted-foreground">
                    {formatFileSize(document.sizeBytes)}
                  </span>
                  {viewed ? (
                    <CheckIcon className="h-4 w-4 shrink-0 text-success" aria-label="Opened" />
                  ) : (
                    <span className="h-4 w-4 shrink-0" aria-hidden="true" />
                  )}
                </button>

                {isSafeDocumentUrl(document.url) ? (
                  <a
                    href={document.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className={buttonVariants({ variant: 'ghost', size: 'icon' })}
                  >
                    <ExternalIcon />
                    <span className="sr-only">Open {label} in a new tab</span>
                  </a>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}

/**
 * Shows one document inline — PDFs in the browser's own viewer, images as images.
 *
 * The link is captured once, when the document is first shown. The page renews every link a
 * minute before it expires; if the viewer followed each renewal, the PDF would reload and jump
 * back to page one while the reviewer was reading page three. Mount it with `key={document.id}`
 * so choosing a different file starts afresh.
 */
export function DocumentViewer({
  document,
  agencyName,
}: {
  document: KybReviewDocument | null;
  agencyName: string;
}) {
  const [src] = useState(() => document?.url ?? '');

  if (!document) {
    return (
      <Card className="flex aspect-square items-center justify-center">
        <EmptyState title="Nothing to preview">Choose a document from the list.</EmptyState>
      </Card>
    );
  }

  const label = documentTypeLabel(document.documentType);
  const kind = previewKind(document.contentType);
  const safe = isSafeDocumentUrl(src);
  const title = `${label} from ${agencyName}`;

  return (
    <Card className="flex flex-col overflow-hidden">
      <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <p className="text-sm font-medium text-foreground">{label}</p>
          <p className="truncate text-xs text-muted-foreground">{document.fileName}</p>
        </div>
        {isSafeDocumentUrl(document.url) ? (
          <a
            href={document.url}
            target="_blank"
            rel="noopener noreferrer"
            className={buttonVariants({ variant: 'outline', size: 'sm' })}
          >
            <ExternalIcon />
            Open in new tab
          </a>
        ) : null}
      </div>

      <div className="flex aspect-square w-full items-center justify-center bg-muted">
        {!safe ? (
          <EmptyState title="This document cannot be shown">
            Its link is not one this console recognises. Reload the page to fetch a fresh one.
          </EmptyState>
        ) : kind === 'pdf' ? (
          <iframe src={src} title={title} className="h-full w-full bg-background" />
        ) : kind === 'image' ? (
          <img src={src} alt={title} className="max-h-full max-w-full object-contain" />
        ) : (
          <EmptyState title="No preview for this file type">
            Open it in a new tab to read it.
          </EmptyState>
        )}
      </div>
    </Card>
  );
}
