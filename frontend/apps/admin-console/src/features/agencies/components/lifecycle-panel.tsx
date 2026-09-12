import { useState } from 'react';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@trips/ui';
import { DownloadIcon } from '../../../components/icons';
import { describeLoadError } from '../../../lib/api/problem';
import { useAuth } from '../../auth/auth-context';
import { formatDateTime } from '../../../lib/format';
import { actionDisplay, availableActions, canExport } from '../agency-rules';
import { useAgencyAction, useAgencyExport } from '../agency-queries';
import type { AgencyAction, AgencyProfile } from '../types';
import { ReasonDialog } from './reason-dialog';

/**
 * Verify, suspend, reinstate and terminate — each behind a reason, each audited.
 *
 * Only the actions that make sense from where the agency is now are offered, and only the ones
 * this account holds the permission for. The API refuses the rest regardless; the point of doing
 * it here too is that a button whose only outcome is a 403 teaches people to ignore buttons.
 *
 * The export sits with them because termination is when it matters: the agency is entitled to
 * their own records, and taking the copy after the relationship ends is an awkward conversation.
 */
export function LifecyclePanel({ profile }: { profile: AgencyProfile }) {
  const { session } = useAuth();
  const permissions = session?.claims.permissions ?? [];

  const [open, setOpen] = useState<AgencyAction | null>(null);
  const act = useAgencyAction(profile.id);
  const exportData = useAgencyExport(profile.id);

  const offered = availableActions(profile.status)
    .map(actionDisplay)
    .filter((display) => permissions.includes(display.permission));

  const exportFailure = exportData.error ? describeLoadError(exportData.error) : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Actions</CardTitle>
        <CardDescription>
          Each one asks for a reason and is recorded in the audit log with your name.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-4">
        {profile.statusReason ? (
          <Alert
            tone={profile.canTakeNewBookings ? 'info' : 'warning'}
            title={`Last change ${formatDateTime(profile.statusChangedAt)}`}
          >
            {profile.statusReason}
          </Alert>
        ) : null}

        {act.isSuccess ? (
          <Alert tone="success" title={`This agency is now ${act.data.status.toLowerCase()}`}>
            {act.data.canTakeNewBookings
              ? 'They can sell again, and their storefront is live.'
              : 'No new bookings, and the storefront is offline. Everything already sold stands.'}
          </Alert>
        ) : null}

        {exportFailure ? (
          <Alert tone="destructive" title={exportFailure.title}>
            {exportFailure.detail}
          </Alert>
        ) : null}

        {offered.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            {profile.status === 'Terminated'
              ? 'This agency has been terminated. There is nothing further to do here.'
              : 'Your account holds none of the permissions these actions need. Ask a Super Admin.'}
          </p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {offered.map((display) => (
              <Button
                key={display.action}
                variant={display.destructive ? 'destructive' : 'outline'}
                onClick={() => {
                  act.reset();
                  setOpen(display.action);
                }}
              >
                {display.label}
              </Button>
            ))}
          </div>
        )}

        {canExport(permissions) ? (
          <div className="flex flex-col gap-2 border-t border-border pt-4">
            <Button
              variant="outline"
              loading={exportData.isPending}
              onClick={() => {
                exportData.mutate(undefined, { onSuccess: save });
              }}
            >
              {exportData.isPending ? null : <DownloadIcon />}
              Export everything this agency owns
            </Button>
            <p className="text-xs text-muted-foreground">
              A JSON file with their profile, staff, wallet, ledger and orders. It contains
              travellers&rsquo; personal data, and taking it is itself recorded in the audit log.
            </p>
          </div>
        ) : null}
      </CardContent>

      {open ? (
        <ReasonDialog
          key={open}
          display={actionDisplay(open)}
          agencyName={profile.name}
          open
          onOpenChange={(next) => {
            if (!next) setOpen(null);
          }}
          pending={act.isPending}
          error={act.error}
          onConfirm={(reason) =>
            act.mutate({ action: open, reason }, { onSuccess: () => setOpen(null) })
          }
        />
      ) : null}
    </Card>
  );
}

/**
 * Hands the export to the browser as a file.
 *
 * A blob and a synthetic click, because the API needs the Authorization header and a plain
 * `<a href>` sends none. The object URL is revoked straight after: without it the whole export
 * stays in memory until the tab is closed.
 */
function save({ fileName, json }: { fileName: string; json: string }) {
  const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
  const anchor = document.createElement('a');

  anchor.href = url;
  anchor.download = fileName;
  anchor.click();

  URL.revokeObjectURL(url);
}
