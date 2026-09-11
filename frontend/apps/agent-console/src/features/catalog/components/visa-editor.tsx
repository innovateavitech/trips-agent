import { Plus } from 'lucide-react';
import { Alert, Button, Input, Select } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import {
  moveItem,
  newKey,
  removeAt,
  replaceAt,
  visaTotalMinor,
  type DocumentDraft,
  type FieldErrors,
  type VisaDraft,
} from '../catalog-rules';
import type { EntryType } from '../types';
import { RowControls } from './editor-parts';

/**
 * #163 — a visa as the agency sells it: the terms, the fees, and the checklist
 * of documents an applicant must bring. Fulfilment is manual, and the screen
 * says so: nothing here files anything with an embassy.
 */
export function VisaEditor({
  visa,
  errors,
  currency,
  onChange,
}: {
  visa: VisaDraft;
  errors: FieldErrors;
  currency: string;
  onChange: (visa: VisaDraft) => void;
}) {
  const change = (patch: Partial<VisaDraft>) => onChange({ ...visa, ...patch });
  const total = visaTotalMinor(visa);

  return (
    <div className="flex flex-col gap-5">
      <div className="grid items-start gap-3 sm:grid-cols-2">
        <Input
          label="Visa type"
          placeholder="Tourist"
          value={visa.visaType}
          onChange={(event) => change({ visaType: event.target.value })}
        />
        <Select
          label="Entry"
          value={visa.entryType}
          onChange={(event) => change({ entryType: event.target.value as EntryType })}
        >
          <option value="Single">Single entry</option>
          <option value="Multiple">Multiple entry</option>
        </Select>
        <Input
          label="Processing time (working days)"
          inputMode="numeric"
          value={visa.processingTimeDays}
          onChange={(event) => change({ processingTimeDays: event.target.value })}
          error={errors['visa.processingTimeDays']}
        />
        <Input
          label="Valid for (days)"
          hint="From the day it is issued"
          inputMode="numeric"
          value={visa.validityDays}
          onChange={(event) => change({ validityDays: event.target.value })}
          error={errors['visa.validityDays']}
        />
        <Input
          label={`Consular fee (${currency})`}
          hint="What the embassy charges"
          inputMode="decimal"
          value={visa.consularFee}
          onChange={(event) => change({ consularFee: event.target.value })}
          error={errors['visa.consularFee']}
        />
        <Input
          label={`Your service fee (${currency})`}
          inputMode="decimal"
          value={visa.serviceFee}
          onChange={(event) => change({ serviceFee: event.target.value })}
          error={errors['visa.serviceFee']}
        />
      </div>

      <p className="text-sm text-muted-foreground">
        Your customer pays{' '}
        <span className="font-semibold tabular-nums text-foreground">
          {total === null ? '—' : formatMoneyShort(total, currency)}
        </span>{' '}
        in fees.
      </p>

      <DocumentChecklist
        documents={visa.documents}
        onChange={(documents) => change({ documents })}
      />

      <Alert tone="info" title="You handle each application">
        Applicants see this checklist before they pay. You prepare and submit each application
        yourself — nothing is sent to an embassy from here.
      </Alert>
    </div>
  );
}

function DocumentChecklist({
  documents,
  onChange,
}: {
  documents: DocumentDraft[];
  onChange: (documents: DocumentDraft[]) => void;
}) {
  return (
    <fieldset className="flex flex-col gap-3">
      <legend className="mb-1 text-sm font-semibold text-foreground">Documents the applicant provides</legend>
      {documents.length === 0 ? (
        <p className="text-sm text-muted-foreground">No documents listed yet.</p>
      ) : (
        <ol className="flex flex-col gap-3">
          {documents.map((document, index) => (
            <li key={document.key} className="flex flex-wrap items-end gap-3 rounded-lg border border-border p-3">
              <div className="min-w-0 flex-1 basis-64">
                <Input
                  label={`Document ${index + 1}`}
                  placeholder="Bank statement for the last three months"
                  value={document.label}
                  onChange={(event) => onChange(replaceAt(documents, index, { label: event.target.value }))}
                />
              </div>
              <label className="flex items-center gap-2 pb-2.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  className="h-4 w-4 accent-primary"
                  checked={document.isMandatory}
                  onChange={(event) =>
                    onChange(replaceAt(documents, index, { isMandatory: event.target.checked }))
                  }
                />
                Required
              </label>
              <div className="pb-0.5">
                <RowControls
                  label={`document ${index + 1}`}
                  index={index}
                  count={documents.length}
                  onMove={(direction) => onChange(moveItem(documents, index, direction))}
                  onRemove={() => onChange(removeAt(documents, index))}
                />
              </div>
            </li>
          ))}
        </ol>
      )}
      <div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onChange([...documents, { key: newKey(), label: '', isMandatory: true }])}
        >
          <Plus className="h-4 w-4" aria-hidden="true" />
          Add a document
        </Button>
      </div>
    </fieldset>
  );
}
