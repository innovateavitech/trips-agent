import { Button, Input, Select } from '@trips/ui';
import type { StatementFilters, WalletTransactionType } from '../types';
import {
  TRANSACTION_TYPES,
  TRANSACTION_TYPE_HINTS,
  TRANSACTION_TYPE_LABELS,
} from '../transaction-display';

/**
 * Filters for the statement: one type, and a date range.
 *
 * A single-select rather than a multi-select, even though `StatementFilters.types`
 * is an array. The array is the shape the API wants and will not change; the
 * control is the simplest thing that covers the actual job ("show me just the
 * top-ups"), and a checkbox list here would be a lot of interface for a
 * question nobody asks in the plural.
 */
export function StatementFiltersBar({
  filters,
  onChange,
}: {
  filters: StatementFilters;
  onChange: (next: StatementFilters) => void;
}) {
  const selectedType = filters.types[0] ?? '';
  const hint = selectedType
    ? TRANSACTION_TYPE_HINTS[selectedType as WalletTransactionType]
    : undefined;
  const isFiltered = filters.types.length > 0 || filters.from !== null || filters.to !== null;

  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-start">
      <div className="sm:w-56">
        <Select
          label="Type"
          value={selectedType}
          hint={hint}
          onChange={(event) =>
            onChange({
              ...filters,
              types: event.target.value ? [event.target.value as WalletTransactionType] : [],
            })
          }
        >
          <option value="">All types</option>
          {TRANSACTION_TYPES.map((type) => (
            <option key={type} value={type}>
              {TRANSACTION_TYPE_LABELS[type]}
            </option>
          ))}
        </Select>
      </div>

      <div className="sm:w-44">
        <Input
          label="From"
          type="date"
          value={filters.from ?? ''}
          max={filters.to ?? undefined}
          onChange={(event) => onChange({ ...filters, from: event.target.value || null })}
        />
      </div>

      <div className="sm:w-44">
        <Input
          label="To"
          type="date"
          value={filters.to ?? ''}
          min={filters.from ?? undefined}
          onChange={(event) => onChange({ ...filters, to: event.target.value || null })}
        />
      </div>

      {isFiltered ? (
        // Aligned to the bottom of the fields, which sit under their labels.
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="sm:mt-7"
          onClick={() => onChange({ types: [], from: null, to: null })}
        >
          Clear filters
        </Button>
      ) : null}
    </div>
  );
}
