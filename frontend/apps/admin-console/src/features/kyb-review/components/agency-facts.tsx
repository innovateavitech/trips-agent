import type { ReactNode } from 'react';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@trips/ui';
import { countryName, formatDateTime } from '../../../lib/format';
import type { KybReviewDetail } from '../types';
import { agencyStatusDisplay, submissionStatusDisplay } from '../status-display';

/**
 * The business being verified, as it described itself. The reviewer's job is to check these
 * facts against the documents beside them, so they are laid out as a plain list of label and
 * value — nothing to interpret, nothing hidden behind a click.
 */
export function AgencyFacts({ detail }: { detail: KybReviewDetail }) {
  const agencyStatus = agencyStatusDisplay(detail.agencyStatus);
  const submissionStatus = submissionStatusDisplay(detail.submissionStatus);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Agency details</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
          <Fact label="Legal name">{detail.legalName}</Fact>
          <Fact label="Trading name">
            {detail.tradingName?.trim() || (
              <span className="text-muted-foreground">None given</span>
            )}
          </Fact>
          <Fact label="Country">
            {countryName(detail.countryCode)}{' '}
            <span className="text-muted-foreground">({detail.countryCode})</span>
          </Fact>
          <Fact label="Submitted">
            <span className="tabular-nums">{formatDateTime(detail.submittedAt)}</span>
          </Fact>
          <Fact label="Agency status">
            <Badge tone={agencyStatus.tone}>{agencyStatus.label}</Badge>
          </Fact>
          <Fact label="Submission status">
            <Badge tone={submissionStatus.tone}>{submissionStatus.label}</Badge>
          </Fact>
          <Fact label="Agency ID" wide>
            <span className="break-all font-mono text-xs text-muted-foreground">
              {detail.agencyId}
            </span>
          </Fact>
        </dl>
      </CardContent>
    </Card>
  );
}

function Fact({ label, children, wide }: { label: string; children: ReactNode; wide?: boolean }) {
  return (
    <div className={wide ? 'flex flex-col gap-1 sm:col-span-2' : 'flex flex-col gap-1'}>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="text-sm text-foreground">{children}</dd>
    </div>
  );
}
