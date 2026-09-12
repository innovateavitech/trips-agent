import { useState } from 'react';
import { Alert, Badge, Button, Card } from '@trips/ui';
import { describeError } from '../../../api/errors';
import {
  useCheckDomainNow,
  useMakeDomainPrimary,
  useRemoveSiteDomain,
  type SiteDomain,
} from '../storefront-queries';

/**
 * One web address, and honestly where it has got to.
 *
 * Verification and the certificate are shown apart because they are apart: a domain can be proved
 * and not yet secured, and telling an agent "not working" when the truth is "proved, waiting on the
 * certificate" sends them back to their registrar to break records that were already right.
 */
export function DomainCard({ domain }: { domain: SiteDomain }) {
  const check = useCheckDomainNow();
  const makePrimary = useMakeDomainPrimary();
  const remove = useRemoveSiteDomain();
  const [showChecks, setShowChecks] = useState(false);

  const error = check.error ?? makePrimary.error ?? remove.error;
  const busy = check.isPending || makePrimary.isPending || remove.isPending;

  const verified = domain.verificationStatus === 'Verified';
  const secured = domain.sslStatus === 'Issued';

  return (
    <Card className="p-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="break-all text-lg font-semibold text-foreground">{domain.hostname}</h2>
            {domain.isPrimary ? <Badge tone="primary">Main address</Badge> : null}
            {domain.type === 'Subdomain' ? <Badge tone="neutral">Free address</Badge> : null}
          </div>

          <div className="mt-2 flex flex-wrap items-center gap-2">
            <Badge tone={verified ? 'success' : domain.needsReview ? 'warning' : 'neutral'}>
              {domain.needsReview
                ? 'Being checked by us'
                : verified
                  ? 'Pointing here'
                  : domain.verificationStatus === 'Abandoned'
                    ? 'Gave up looking'
                    : 'Waiting for your DNS records'}
            </Badge>

            <Badge
              tone={secured ? 'success' : domain.sslStatus === 'Failed' ? 'destructive' : 'neutral'}
            >
              {secured
                ? 'Secure (https)'
                : domain.sslStatus === 'Failed'
                  ? 'Certificate failed'
                  : domain.sslStatus === 'Pending'
                    ? 'Getting a certificate'
                    : 'Not secured yet'}
            </Badge>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {domain.canCheckNow ? (
            <Button
              variant="outline"
              size="sm"
              onClick={() => check.mutate(domain.id)}
              disabled={busy}
            >
              {check.isPending ? 'Looking…' : 'Check now'}
            </Button>
          ) : null}

          {domain.canMakePrimary ? (
            <Button
              variant="outline"
              size="sm"
              onClick={() => makePrimary.mutate(domain.id)}
              disabled={busy}
            >
              Make this my main address
            </Button>
          ) : null}

          {domain.canRemove ? (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => remove.mutate(domain.id)}
              disabled={busy}
            >
              Remove
            </Button>
          ) : null}
        </div>
      </div>

      {error ? (
        <Alert tone="destructive" className="mt-4">
          {describeError(error).title}
        </Alert>
      ) : null}

      {domain.sslLastError ? (
        <Alert tone="warning" className="mt-4">
          {domain.sslLastError}
        </Alert>
      ) : null}

      {domain.dnsRecords.length > 0 && !verified ? (
        <div className="mt-5">
          <h3 className="text-sm font-semibold text-foreground">
            Create these two records where you bought your domain
          </h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Most registrars have a &ldquo;DNS&rdquo; or &ldquo;Advanced DNS&rdquo; section. Changes
            usually take a few minutes, occasionally a few hours.
          </p>

          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-128 text-left text-sm">
              <thead>
                <tr className="border-b border-border text-xs uppercase tracking-wide text-muted-foreground">
                  <th scope="col" className="py-2 pr-4 font-medium">
                    Type
                  </th>
                  <th scope="col" className="py-2 pr-4 font-medium">
                    Host
                  </th>
                  <th scope="col" className="py-2 font-medium">
                    Value
                  </th>
                </tr>
              </thead>
              <tbody>
                {domain.dnsRecords.map((record) => (
                  <tr
                    key={`${record.recordType}-${record.name}`}
                    className="border-b border-border last:border-0"
                  >
                    <td className="py-3 pr-4 font-medium text-foreground">{record.recordType}</td>
                    <td className="py-3 pr-4">
                      {/*
                        The label without the domain on the end, which is what a registrar's "Host"
                        box wants. Typing the full name there is the commonest way a record ends up
                        at name.example.com.example.com and never resolves.
                      */}
                      <code className="break-all text-foreground">{record.hostLabel}</code>
                    </td>
                    <td className="py-3">
                      <code className="break-all text-foreground">{record.value}</code>
                      <p className="mt-1 text-xs text-muted-foreground">{record.purpose}</p>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {domain.nextCheckAt ? (
            <p className="mt-3 text-sm text-muted-foreground">
              We look again automatically — next at{' '}
              {new Date(domain.nextCheckAt).toLocaleTimeString()}. You do not have to stay on this
              page.
            </p>
          ) : null}
        </div>
      ) : null}

      {domain.recentChecks.length > 0 ? (
        <div className="mt-5">
          <Button variant="ghost" size="sm" onClick={() => setShowChecks(!showChecks)}>
            {showChecks ? 'Hide what we found' : 'Show what we found'}
          </Button>

          {showChecks ? (
            <ul className="mt-3 space-y-3 text-sm">
              {domain.recentChecks.map((entry) => (
                <li
                  key={`${entry.checkedAt}-${entry.recordType}`}
                  className="border-l-2 border-border pl-3"
                >
                  <p className="text-muted-foreground">
                    {new Date(entry.checkedAt).toLocaleString()} · {entry.recordType} record on{' '}
                    <code className="break-all">{entry.recordName}</code>
                  </p>
                  <p className="text-foreground">
                    {entry.outcome === 'Match'
                      ? 'Found what we expected.'
                      : entry.outcome === 'NotFound'
                        ? 'No such record yet.'
                        : entry.outcome === 'Mismatch'
                          ? `Found something else: ${entry.observed.join(', ')}`
                          : `Could not check: ${entry.detail ?? entry.outcome.toLowerCase()}`}
                  </p>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}
    </Card>
  );
}
