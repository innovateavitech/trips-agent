import { FileCheck2 } from 'lucide-react';
import { Button } from '@trips/ui';
import type { KybDocument } from '../types';
import { formatBytes } from '../upload-rules';

export interface DocumentCardProps {
  document: KybDocument;
  canReplace: boolean;
  onReplace: () => void;
}

/** One uploaded document: what it is, and the way back if it is the wrong file. */
export function DocumentCard({ document, canReplace, onReplace }: DocumentCardProps) {
  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-muted/40 px-4 py-3">
      <FileCheck2 className="size-5 shrink-0 text-success" aria-hidden="true" />

      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium text-foreground" title={document.fileName}>
          {document.fileName}
        </p>
        <p className="text-xs text-muted-foreground">
          {formatBytes(document.sizeBytes)}
          {document.uploadedAt.length > 0 && ` · uploaded ${uploadedOn(document.uploadedAt)}`}
        </p>
      </div>

      {canReplace && (
        <Button variant="outline" size="sm" onClick={onReplace}>
          Replace
        </Button>
      )}
    </div>
  );
}

function uploadedOn(iso: string): string {
  const at = new Date(iso);
  return Number.isNaN(at.getTime())
    ? ''
    : at.toLocaleDateString(undefined, {
        day: 'numeric',
        month: 'short',
        hour: '2-digit',
        minute: '2-digit',
      });
}
