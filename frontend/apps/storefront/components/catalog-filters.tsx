import Link from 'next/link';
import { formatMoneyShort, int64 } from '@trips/utils';
import type { Catalog } from '../lib/api';

/**
 * The choices a traveller can narrow the catalog by.
 *
 * A plain `GET` form with no JavaScript behind it. Every choice ends up in the URL, which means the
 * result can be linked to, shared, bookmarked and rendered on the server — and it works on a phone
 * with a bad connection, which is most of the audience. The values offered are the ones this agency
 * actually sells, never a fixed list.
 */
export function CatalogFilters({
  filters,
  selected,
  currency,
}: {
  filters: Catalog['filters'];
  selected: Record<string, string | undefined>;
  currency: string;
}) {
  const hasAnything =
    filters.productTypes.length > 0 ||
    filters.destinations.length > 0 ||
    filters.categories.length > 0;

  if (!hasAnything) {
    return null;
  }

  const isFiltered = Object.values(selected).some((value) => value);

  return (
    <form
      method="get"
      aria-label="Narrow these results"
      className="mb-8 grid gap-4 rounded-lg border border-border bg-card p-4 sm:grid-cols-2 lg:grid-cols-4"
    >
      <Field label="Search">
        <input
          type="search"
          name="q"
          defaultValue={selected.q ?? ''}
          placeholder="Where would you like to go?"
          className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
      </Field>

      {filters.productTypes.length > 0 && (
        <Field label="Type">
          <Choice
            name="type"
            value={selected.type}
            options={filters.productTypes}
            anyLabel="Any type"
          />
        </Field>
      )}

      {filters.destinations.length > 0 && (
        <Field label="Destination">
          <Choice
            name="destination"
            value={selected.destination}
            options={filters.destinations}
            anyLabel="Anywhere"
          />
        </Field>
      )}

      {filters.categories.length > 0 && (
        <Field label="Category">
          <Choice
            name="category"
            value={selected.category}
            options={filters.categories}
            anyLabel="Any category"
          />
        </Field>
      )}

      {filters.maxPriceMinor != null && (
        <Field
          label={`Up to ${formatMoneyShort(int64(filters.maxPriceMinor), currency)}`}
          hint={
            filters.minPriceMinor != null
              ? `from ${formatMoneyShort(int64(filters.minPriceMinor), currency)}`
              : undefined
          }
        >
          <input
            type="number"
            name="maxPrice"
            inputMode="numeric"
            min={0}
            step={100}
            defaultValue={selected.maxPrice ?? ''}
            placeholder="Your budget, in kobo"
            className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </Field>
      )}

      <div className="flex items-end gap-3 sm:col-span-2 lg:col-span-4">
        <button
          type="submit"
          className="inline-flex h-10 items-center justify-center rounded-md bg-primary px-5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        >
          Show results
        </button>

        {isFiltered && (
          <Link
            href="/tours"
            className="text-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
          >
            Clear
          </Link>
        )}
      </div>
    </form>
  );
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-xs font-medium text-muted-foreground">
        {label}
        {hint && <span className="ml-1 font-normal">({hint})</span>}
      </span>
      {children}
    </label>
  );
}

function Choice({
  name,
  value,
  options,
  anyLabel,
}: {
  name: string;
  value: string | undefined;
  options: readonly string[];
  anyLabel: string;
}) {
  return (
    <select
      name={name}
      defaultValue={value ?? ''}
      className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <option value="">{anyLabel}</option>
      {options.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  );
}
