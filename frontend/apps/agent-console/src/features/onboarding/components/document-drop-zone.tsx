import { useRef, useState, type DragEvent } from 'react';
import { UploadCloud } from 'lucide-react';
import { cn } from '@trips/ui';
import type { UploadLimits } from '../types';
import { describeAccepted, formatBytes, rejectionFor } from '../upload-rules';

export interface DocumentDropZoneProps {
  documentType: string;
  limits: UploadLimits;
  disabled?: boolean;
  isUploading?: boolean;
  onFile: (file: File) => void;
}

/**
 * Drag a file in, or press to choose one.
 *
 * The file is checked here, before anything is sent: size, extension and the
 * content type the browser reports. A refusal is shown in place, next to the
 * document it was meant for, and nothing about the rest of the page moves.
 *
 * It is a real `<input type="file">` under a label, so the keyboard, screen
 * readers and password managers all behave normally — a `<div>` with a click
 * handler looks the same and is unusable without a mouse.
 */
export function DocumentDropZone({
  documentType,
  limits,
  disabled = false,
  isUploading = false,
  onFile,
}: DocumentDropZoneProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [isOver, setIsOver] = useState(false);
  const [rejection, setRejection] = useState<string | null>(null);

  function offer(file: File | undefined) {
    if (!file || disabled) return;

    const refusal = rejectionFor(file, limits);
    setRejection(refusal);

    if (refusal === null) {
      onFile(file);
    }
  }

  function onDrop(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setIsOver(false);
    offer(event.dataTransfer.files[0]);
  }

  const inputId = `upload-${documentType}`;

  return (
    <div className="flex flex-col gap-2">
      <label
        htmlFor={inputId}
        onDragOver={(event) => {
          event.preventDefault();
          if (!disabled) setIsOver(true);
        }}
        onDragLeave={() => setIsOver(false)}
        onDrop={onDrop}
        className={cn(
          'flex cursor-pointer flex-col items-center gap-2 rounded-lg border border-dashed border-border px-6 py-8 text-center transition-colors',
          'hover:border-primary/60 hover:bg-accent focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2',
          isOver && 'border-primary bg-accent',
          disabled && 'pointer-events-none opacity-60',
        )}
      >
        <UploadCloud className="size-6 text-muted-foreground" aria-hidden="true" />
        <span className="text-sm font-medium text-foreground">
          {isUploading ? 'Uploading…' : 'Drag a file here, or choose one'}
        </span>
        <span className="text-xs text-muted-foreground">
          {describeAccepted(limits)}, up to {formatBytes(limits.maxSizeBytes)}
        </span>
        <input
          id={inputId}
          ref={inputRef}
          type="file"
          className="sr-only"
          accept={limits.allowedExtensions.join(',')}
          disabled={disabled || isUploading}
          onChange={(event) => {
            offer(event.target.files?.[0]);
            // Cleared so choosing the same file again still fires a change.
            event.target.value = '';
          }}
        />
      </label>

      {rejection !== null && (
        <p role="alert" className="text-sm text-destructive">
          {rejection}
        </p>
      )}
    </div>
  );
}
