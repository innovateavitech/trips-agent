import { Check, Trash2, X } from 'lucide-react';
import { useState } from 'react';
import { Button, Input } from '@trips/ui';
import { newKey, removeAt, type InclusionDraft } from '../catalog-rules';
import type { InclusionKind } from '../types';

/**
 * issue 162 — what the price covers, and what it does not. The "not included" list
 * matters as much: it is what stops a customer arguing about the flights later.
 */
export function InclusionsEditor({
  inclusions,
  onChange,
}: {
  inclusions: InclusionDraft[];
  onChange: (inclusions: InclusionDraft[]) => void;
}) {
  return (
    <div className="grid items-start gap-6 md:grid-cols-2">
      <InclusionList
        kind="Inclusion"
        title="Included"
        addLabel="Add something included"
        placeholder="Airport transfers both ways"
        all={inclusions}
        onChange={onChange}
      />
      <InclusionList
        kind="Exclusion"
        title="Not included"
        addLabel="Add something not included"
        placeholder="International flights"
        all={inclusions}
        onChange={onChange}
      />
    </div>
  );
}

function InclusionList({
  kind,
  title,
  addLabel,
  placeholder,
  all,
  onChange,
}: {
  kind: InclusionKind;
  title: string;
  addLabel: string;
  placeholder: string;
  all: InclusionDraft[];
  onChange: (inclusions: InclusionDraft[]) => void;
}) {
  const [text, setText] = useState('');
  const rows = all.map((item, index) => ({ item, index })).filter(({ item }) => item.kind === kind);
  const Icon = kind === 'Inclusion' ? Check : X;

  function add() {
    const line = text.trim();
    if (!line) return;
    onChange([...all, { key: newKey(), kind, text: line }]);
    setText('');
  }

  return (
    <div className="flex flex-col gap-3">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      {rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nothing yet.</p>
      ) : (
        <ul
          aria-label={title}
          className="flex flex-col divide-y divide-border rounded-md border border-border"
        >
          {rows.map(({ item, index }) => (
            <li
              key={item.key}
              className="flex items-center justify-between gap-2 py-1 pl-3 pr-1 text-sm"
            >
              <span className="flex min-w-0 items-center gap-2 text-foreground">
                <Icon
                  aria-hidden="true"
                  className={
                    kind === 'Inclusion'
                      ? 'h-4 w-4 shrink-0 text-success-subtle-foreground'
                      : 'h-4 w-4 shrink-0 text-muted-foreground'
                  }
                />
                {item.text}
              </span>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                aria-label={`Remove “${item.text}”`}
                onClick={() => onChange(removeAt(all, index))}
              >
                <Trash2 className="h-4 w-4" aria-hidden="true" />
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex items-end gap-2">
        <div className="min-w-0 flex-1">
          <Input
            label={addLabel}
            placeholder={placeholder}
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                add();
              }
            }}
          />
        </div>
        <Button type="button" variant="outline" onClick={add} disabled={!text.trim()}>
          Add
        </Button>
      </div>
    </div>
  );
}
