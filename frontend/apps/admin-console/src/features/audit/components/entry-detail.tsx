import type { ReactNode } from 'react';
import { Alert, Badge, Dialog, DialogContent, DialogDescription, DialogTitle } from '@trips/ui';
import { formatDateTime } from '../../../lib/format';
import {
  actionLabel,
  actionTone,
  actorTypeDisplay,
  changedFields,
  fieldLabel,
  stateIsUnreadable,
} from '../audit-rules';
import type { AuditLogEntry } from '../types';

/**
 * One entry in full: who, when, why, and exactly which columns moved.
 *
 * Only the fields that changed are listed. The row records the whole entity on both sides, and
 * showing all of it would bury the one edited column among forty that stayed put — which is the
 * difference between a record somebody can act on and a record somebody scrolls past.
 */
export function EntryDetail({
  entry,
  onClose,
}: {
  entry: AuditLogEntry | null;
  onClose: () => void;
}) {
  if (!entry) return null;

  const changes = changedFields(entry);
  const actor = actorTypeDisplay(entry.actorType);

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className="max-w-2xl" aria-describedby={undefined}>
        <DialogTitle className="flex flex-wrap items-center gap-2">
          {actionLabel(entry.action)}
          <Badge tone={actionTone(entry.action)}>{entry.entityType}</Badge>
        </DialogTitle>

        <DialogDescription>
          {entry.actorName} · {formatDateTime(entry.occurredAt)}
        </DialogDescription>

        <dl className="grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
          <Fact term="Acting as">
            <Badge tone={actor.tone}>{actor.label}</Badge>
          </Fact>
          <Fact term="Agency">{entry.agencyName ?? 'The platform'}</Fact>
          <Fact term="Entity">
            <span className="break-all font-mono text-xs">{entry.entityId}</span>
          </Fact>
          {/* Shown because it is recorded, and somebody investigating an account takeover needs
              it. It is the only place in the console an IP address appears. */}
          <Fact term="From">{entry.actorIpAddress ?? 'Not recorded'}</Fact>
        </dl>

        {entry.reason ? (
          <div className="rounded-md bg-muted p-3">
            <p className="text-xs font-medium text-muted-foreground">Reason given</p>
            <p className="mt-1 whitespace-pre-wrap text-sm text-foreground">{entry.reason}</p>
          </div>
        ) : null}

        <section className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold text-foreground">What changed</h3>

          {stateIsUnreadable(entry) ? (
            <Alert tone="warning" title="The recorded state cannot be read">
              The row holds something that is not the JSON object this screen expects. The rest of
              the entry — who, when and why — is unaffected and is shown above.
            </Alert>
          ) : null}

          {!stateIsUnreadable(entry) && changes.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              Nothing recorded on either side. Some actions — an export, a sign-in — change no
              columns; they are recorded because they happened, not because something moved.
            </p>
          ) : null}

          {changes.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full min-w-96 border-collapse text-sm">
                <thead>
                  <tr className="border-b border-border text-left">
                    <th scope="col" className="py-2 pr-3 font-medium">
                      Field
                    </th>
                    <th scope="col" className="py-2 pr-3 font-medium">
                      Before
                    </th>
                    <th scope="col" className="py-2 font-medium">
                      After
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {changes.map((change) => (
                    <tr key={change.field} className="border-b border-border last:border-0">
                      <td className="py-2 pr-3 align-top font-medium text-foreground">
                        {fieldLabel(change.field)}
                      </td>
                      <td className="py-2 pr-3 align-top text-muted-foreground">
                        <span className="break-words">{change.before ?? '—'}</span>
                      </td>
                      <td className="py-2 align-top text-foreground">
                        <span className="break-words">{change.after ?? '—'}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </section>
      </DialogContent>
    </Dialog>
  );
}

function Fact({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{term}</dt>
      <dd className="mt-0.5 text-foreground">{children}</dd>
    </div>
  );
}
