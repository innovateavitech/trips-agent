import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Textarea,
} from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { useEraseCustomer, useErasureRequests, usePreviewErasure } from '../erasure-queries';
import { MINIMUM_REASON, canErase, describeChanges, statusTone } from '../erasure-rules';
import type { ErasurePreview, ErasureRequestRecord, ErasureResult } from '../types';

/**
 * Erasing one person's details on request (NDPA, issue 106).
 *
 * Three steps, in the order somebody actually works: find the person, read what would change, then
 * say why and confirm. The find step is separate because erasing the wrong person cannot be undone
 * — the screen shows the name and the address it matched before it will let anyone go further.
 *
 * What it does and what it deliberately leaves behind is ADR-0009; the wording here repeats the
 * parts an operator has to be able to explain to whoever asked.
 */
export function DataErasurePage() {
  useDocumentTitle('Data erasure');

  const [agencyId, setAgencyId] = useState('');
  const [email, setEmail] = useState('');
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [notFound, setNotFound] = useState(false);

  const preview = usePreviewErasure();
  const erase = useEraseCustomer();
  const requests = useErasureRequests();

  const found = preview.data ?? null;
  const blocked = (found?.blockers.length ?? 0) > 0;
  const ready = canErase(found, reason, confirmed);

  function find(event: React.FormEvent) {
    event.preventDefault();

    setNotFound(false);
    erase.reset();

    preview.mutate(
      { agencyId: agencyId.trim(), email: email.trim() },
      { onSuccess: (result) => setNotFound(result === null) },
    );
  }

  function run() {
    if (!found || !ready) return;

    erase.mutate(
      { agencyId: agencyId.trim(), customerId: found.customerId, reason: reason.trim() },
      {
        onSuccess: () => {
          setConfirmed(false);
          setReason('');
          preview.reset();
        },
      },
    );
  }

  return (
    <Page>
      <PageHeader
        title="Data erasure"
        description="Replace one person's details everywhere, on their request, and keep the financial and audit record."
      />

      <Alert tone="warning" title="This cannot be undone">
        There is no copy of what an erasure destroys, and no way back short of a database restore.
        The money, the orders and the audit trail all stay exactly as they are — what goes is the
        person: their name, their contact details and their travel documents. See ADR-0009.
      </Alert>

      <Card>
        <CardHeader>
          <CardTitle>Find the person</CardTitle>
        </CardHeader>
        <CardContent>
          <form className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end" onSubmit={find}>
            <Input
              label="Agency"
              placeholder="The agency's id"
              value={agencyId}
              onChange={(event) => setAgencyId(event.target.value)}
              hint="From the agency directory."
            />
            <Input
              label="Email address"
              type="email"
              placeholder="what they gave the agency"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              hint="The only way to find somebody: passport numbers are encrypted and cannot be searched."
            />
            <Button
              type="submit"
              variant="outline"
              loading={preview.isPending}
              disabled={!agencyId.trim() || !email.trim()}
            >
              Look up
            </Button>
          </form>

          {preview.isError ? (
            <div className="mt-4">
              <ErrorState {...describeLoadError(preview.error)} />
            </div>
          ) : null}

          {notFound ? (
            <div className="mt-4">
              <EmptyState title="No customer of that agency has that address">
                Check the agency and the address. A traveller who booked as a guest is recorded
                against the address they gave at checkout.
              </EmptyState>
            </div>
          ) : null}
        </CardContent>
      </Card>

      {found ? <FoundPerson person={found} /> : null}

      {found ? (
        <Card>
          <CardHeader>
            <CardTitle>Erase {found.name}</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {blocked ? (
              <Alert tone="warning" title="This cannot be done yet">
                <ul className="list-disc space-y-1 pl-5">
                  {found.blockers.map((blocker) => (
                    <li key={blocker}>{blocker}</li>
                  ))}
                </ul>
              </Alert>
            ) : null}

            <Textarea
              label="Why"
              rows={3}
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              placeholder="Their request by email on 12 September, a regulator's direction, a court order …"
              hint={`At least ${MINIMUM_REASON} characters. It is the only record of why somebody's details were destroyed.`}
            />

            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                className="mt-1"
                checked={confirmed}
                onChange={(event) => setConfirmed(event.target.checked)}
              />
              <span>
                I understand this cannot be undone, and that the agency loses this customer&apos;s
                history.
              </span>
            </label>

            <Button variant="destructive" loading={erase.isPending} disabled={!ready} onClick={run}>
              Erase this person
            </Button>

            {erase.isError ? <ErrorState {...describeLoadError(erase.error)} /> : null}
          </CardContent>
        </Card>
      ) : null}

      {erase.data ? <Outcome result={erase.data} /> : null}

      <Card>
        <CardHeader>
          <CardTitle>Erasures already carried out</CardTitle>
        </CardHeader>
        <CardContent>
          {requests.isError ? (
            <ErrorState
              {...describeLoadError(requests.error)}
              onRetry={() => void requests.refetch()}
              retrying={requests.isFetching}
            />
          ) : (requests.data?.length ?? 0) === 0 ? (
            <EmptyState title="Nobody has been erased yet">
              Every erasure is recorded here, with who ran it and why. The record holds no name,
              email or phone number — it outlives the details it destroyed.
            </EmptyState>
          ) : (
            <RequestTable requests={requests.data ?? []} />
          )}
        </CardContent>
      </Card>
    </Page>
  );
}

/** Who was matched, and what an erasure would touch. */
function FoundPerson({ person }: { person: ErasurePreview }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{person.name}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm text-muted-foreground">{person.email ?? 'No email on record'}</p>

        <dl className="grid grid-cols-2 gap-4 sm:grid-cols-5">
          <Fact label="Orders" value={person.orders} note="kept, with their money" />
          <Fact label="Travellers" value={person.travellers} note="names replaced" />
          <Fact label="Travel documents" value={person.travelDocuments} note="destroyed" />
          <Fact label="Messages" value={person.notifications} note="recipient replaced" />
          <Fact label="Uploaded files" value={person.evidenceFiles} note="deleted from storage" />
        </dl>
      </CardContent>
    </Card>
  );
}

function Fact({ label, value, note }: { label: string; value: number; note: string }) {
  return (
    <div>
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd className="text-2xl font-semibold text-foreground">{value}</dd>
      <p className="text-xs text-muted-foreground">{note}</p>
    </div>
  );
}

/** What the erasure just did, or why it was refused. */
function Outcome({ result }: { result: ErasureResult }) {
  if (!result.completed) {
    return (
      <Alert tone="warning" title="Nothing was changed">
        <ul className="list-disc space-y-1 pl-5">
          {result.blockers.map((blocker) => (
            <li key={blocker}>{blocker}</li>
          ))}
        </ul>
      </Alert>
    );
  }

  return (
    <Alert tone="success" title="Done. Their details are gone.">
      <p>
        The order, the money and the audit trail are untouched. What changed:{' '}
        {describeChanges(result)}.
      </p>
    </Alert>
  );
}

function RequestTable({ requests }: { requests: ErasureRequestRecord[] }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>When</TableHead>
          <TableHead>Agency</TableHead>
          <TableHead>Status</TableHead>
          <TableHead>Reason given</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {requests.map((request) => (
          <TableRow key={request.id}>
            <TableCell>{new Date(request.requestedAt).toLocaleString()}</TableCell>
            <TableCell className="font-mono text-xs">{request.agencyId}</TableCell>
            <TableCell>
              <Badge tone={statusTone(request.status)}>{request.status}</Badge>
            </TableCell>
            <TableCell>{request.refusalReason ?? request.reason}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
