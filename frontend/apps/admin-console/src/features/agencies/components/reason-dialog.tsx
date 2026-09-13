import { useEffect, useState, type FormEvent } from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  Textarea,
} from '@trips/ui';
import { describeLoadError } from '../../../lib/api/problem';
import { MAX_REASON_LENGTH, validateReason, type ActionDisplay } from '../agency-rules';

/**
 * The one dialog behind every lifecycle action: what will happen, and why you are doing it.
 *
 * Three things it is doing deliberately.
 *
 * **The consequences are listed before the box.** Suspension is the action people get wrong —
 * it sounds like "switch them off" and it is not: the bookings an agency has already sold stand,
 * and the travellers on them keep their documents. An admin should read that before they type,
 * not find out afterwards.
 *
 * **The reason is required, and checked here first.** The API refuses a thin one, so refusing it
 * in the browser saves a round trip; the server checks it again regardless.
 *
 * **It is a modal, which this codebase uses sparingly.** These four actions change whether a
 * business can trade, and there is no undo on this screen — that is the bar a modal is for.
 *
 * Mount it with `key={action}`: the draft reason belongs to one decision and must never still be
 * in the box when a different one is opened.
 */
export function ReasonDialog({
  display,
  agencyName,
  open,
  onOpenChange,
  onConfirm,
  pending,
  error,
}: {
  display: ActionDisplay;
  agencyName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: (reason: string) => void;
  pending: boolean;
  error: unknown;
}) {
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string>();

  // Cleared as it closes rather than as it opens: clearing on open would wipe the box under
  // somebody who reopened it to finish a sentence they were part way through.
  useEffect(() => {
    if (!open) {
      setReason('');
      setProblem(undefined);
    }
  }, [open]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setProblem(invalid);

    if (!invalid) onConfirm(reason.trim());
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>{display.title}</DialogTitle>
        <DialogDescription>
          {agencyName}. This is recorded in the audit log against your name.
        </DialogDescription>

        <ul className="flex list-disc flex-col gap-1.5 pl-5 text-sm text-muted-foreground">
          {display.consequences.map((consequence) => (
            <li key={consequence}>{consequence}</li>
          ))}
        </ul>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={problem}
            hint="Written for whoever reads this in a year, not for the person next to you."
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button
              type="submit"
              variant={display.destructive ? 'destructive' : 'primary'}
              loading={pending}
            >
              {display.confirmLabel}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
