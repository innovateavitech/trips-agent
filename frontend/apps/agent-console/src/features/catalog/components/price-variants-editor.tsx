import { Plus } from 'lucide-react';
import { Button, Input, Select } from '@trips/ui';
import {
  emptyVariant,
  moveItem,
  removeAt,
  replaceAt,
  type FieldErrors,
  type VariantDraft,
} from '../catalog-rules';
import type { PaxType } from '../types';
import { RowControls } from './editor-parts';

const PAX_TYPES: ReadonlyArray<{ value: PaxType; label: string }> = [
  { value: 'Adult', label: 'Adult' },
  { value: 'Child', label: 'Child' },
  { value: 'Infant', label: 'Infant' },
];

/**
 * #162 — prices by room, age and group size: "Double room, per adult",
 * "Child 2–11", "Groups of 10 or more". With none, the "from" price is the price.
 */
export function PriceVariantsEditor({
  variants,
  errors,
  currency,
  onChange,
}: {
  variants: VariantDraft[];
  errors: FieldErrors;
  currency: string;
  onChange: (variants: VariantDraft[]) => void;
}) {
  return (
    <div className="flex flex-col gap-4">
      {variants.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          One price for everyone: the &ldquo;from&rdquo; price in Basics. Add prices here when they
          differ by room, age or group size.
        </p>
      ) : null}

      {variants.map((variant, index) => {
        const change = (patch: Partial<VariantDraft>) => onChange(replaceAt(variants, index, patch));
        const error = (field: string) => errors[`variants.${index}.${field}`];

        return (
          <div key={variant.key} className="flex flex-col gap-3 rounded-lg border border-border p-4">
            <div className="flex items-center justify-between gap-2">
              <h3 className="text-sm font-semibold text-foreground">
                {variant.name.trim() || `Price ${index + 1}`}
              </h3>
              <RowControls
                label={`price ${index + 1}`}
                index={index}
                count={variants.length}
                onMove={(direction) => onChange(moveItem(variants, index, direction))}
                onRemove={() => onChange(removeAt(variants, index))}
              />
            </div>
            <div className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-6">
              <div className="sm:col-span-2 lg:col-span-3">
                <Input
                  label="Name"
                  placeholder="Double room, per adult"
                  value={variant.name}
                  onChange={(event) => change({ name: event.target.value })}
                />
              </div>
              <div className="lg:col-span-1">
                <Select
                  label="Traveller"
                  value={variant.paxType}
                  onChange={(event) => change({ paxType: event.target.value as PaxType })}
                >
                  {PAX_TYPES.map((type) => (
                    <option key={type.value} value={type.value}>
                      {type.label}
                    </option>
                  ))}
                </Select>
              </div>
              <div className="lg:col-span-2">
                <Input
                  label={`Price (${currency})`}
                  inputMode="decimal"
                  value={variant.price}
                  onChange={(event) => change({ price: event.target.value })}
                  error={error('price')}
                />
              </div>
              <div className="lg:col-span-2">
                <Input
                  label="People per room"
                  hint="Only if the price depends on it"
                  inputMode="numeric"
                  value={variant.occupancy}
                  onChange={(event) => change({ occupancy: event.target.value })}
                  error={error('occupancy')}
                />
              </div>
              <div className="lg:col-span-2">
                <Input
                  label="Group from"
                  inputMode="numeric"
                  value={variant.minGroupSize}
                  onChange={(event) => change({ minGroupSize: event.target.value })}
                  error={error('minGroupSize')}
                />
              </div>
              <div className="lg:col-span-2">
                <Input
                  label="Group up to"
                  inputMode="numeric"
                  value={variant.maxGroupSize}
                  onChange={(event) => change({ maxGroupSize: event.target.value })}
                  error={error('maxGroupSize')}
                />
              </div>
            </div>
          </div>
        );
      })}

      <div>
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...variants, emptyVariant()])}>
          <Plus className="h-4 w-4" aria-hidden="true" />
          Add a price
        </Button>
      </div>
    </div>
  );
}
