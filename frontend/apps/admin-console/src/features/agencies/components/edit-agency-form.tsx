import { useState, type FormEvent } from 'react';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Input,
  Textarea,
} from '@trips/ui';
import { describeLoadError, fieldError } from '../../../lib/api/problem';
import { useUpdateAgency } from '../agency-queries';
import { MAX_REASON_LENGTH, validateReason } from '../agency-rules';
import type { AgencyProfile } from '../types';

/**
 * Editing the business details KYB verified, with a reason that goes into the audit log.
 *
 * The slug is shown but not editable. It is in storefront URLs and in links travellers already
 * hold, so changing it is a migration rather than an edit, and offering a box for it would invite
 * somebody to break every link an agency has handed out.
 *
 * The VAT rate is entered in basis points, the same integer the API stores, because that is the
 * only representation of a rate in this system — the same reason money is kobo.
 */
export function EditAgencyForm({
  profile,
  onDone,
}: {
  profile: AgencyProfile;
  onDone: () => void;
}) {
  const update = useUpdateAgency(profile.id);

  const [legalName, setLegalName] = useState(profile.legalName);
  const [tradingName, setTradingName] = useState(profile.tradingName ?? '');
  const [taxId, setTaxId] = useState(profile.taxId ?? '');
  const [timezone, setTimezone] = useState(profile.timezone);
  const [vat, setVat] = useState(String(profile.vatRateBasisPoints));
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setReasonProblem(invalid);
    if (invalid) return;

    update.mutate(
      {
        legalName: legalName.trim(),
        tradingName: tradingName.trim() === '' ? null : tradingName.trim(),
        taxId: taxId.trim() === '' ? null : taxId.trim(),
        timezone: timezone.trim(),
        vatRateBasisPoints: Number(vat),
        reason: reason.trim(),
      },
      { onSuccess: onDone },
    );
  }

  const failure = update.error ? describeLoadError(update.error) : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Edit business details</CardTitle>
        <CardDescription>
          What changes here is written to the audit log beside what it was before.
        </CardDescription>
      </CardHeader>

      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          {failure ? (
            <Alert tone="destructive" title={failure.title}>
              {failure.detail}
            </Alert>
          ) : null}

          <div className="grid gap-4 sm:grid-cols-2">
            <Input
              label="Legal name"
              required
              value={legalName}
              error={fieldError(update.error, 'legalName')}
              hint="The name on the incorporation documents."
              onChange={(event) => setLegalName(event.target.value)}
            />

            <Input
              label="Trading name"
              value={tradingName}
              hint="What they actually trade as, if it differs. Travellers see this one."
              onChange={(event) => setTradingName(event.target.value)}
            />

            <Input
              label="Tax ID"
              value={taxId}
              onChange={(event) => setTaxId(event.target.value)}
            />

            <Input
              label="Timezone"
              required
              value={timezone}
              hint="An IANA zone, e.g. Africa/Lagos."
              error={fieldError(update.error, 'timezone')}
              onChange={(event) => setTimezone(event.target.value)}
            />

            <Input
              label="VAT rate (basis points)"
              required
              inputMode="numeric"
              min={0}
              max={10000}
              type="number"
              value={vat}
              hint="750 is 7.5%. An integer, so there is one representation and no rounding to argue about."
              error={fieldError(update.error, 'vatRateBasisPoints')}
              onChange={(event) => setVat(event.target.value)}
            />

            <Input
              label="Storefront slug"
              value={profile.slug}
              readOnly
              disabled
              hint="Not editable: it is in storefront URLs and in links travellers already hold."
            />
          </div>

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem ?? fieldError(update.error, 'reason')}
            hint="Recorded against your name, next to what changed."
            onChange={(event) => setReason(event.target.value)}
          />

          <div className="flex flex-wrap gap-2">
            <Button type="submit" loading={update.isPending}>
              Save changes
            </Button>
            <Button type="button" variant="outline" onClick={onDone}>
              Cancel
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
