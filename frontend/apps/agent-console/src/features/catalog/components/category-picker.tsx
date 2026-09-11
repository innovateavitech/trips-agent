import { useState } from 'react';
import { Button, Input, Select } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { cx } from '../../search/class-names';
import { useCategories, useCreateCategory } from '../catalog-api';
import type { CategoryType } from '../types';

const GROUPS: ReadonlyArray<{ type: CategoryType; label: string }> = [
  { type: 'Category', label: 'Categories' },
  { type: 'Theme', label: 'Themes' },
];

/**
 * #162 — categories ("International packages") and themes ("Beach"). The
 * storefront filters by them, so a product nobody tagged is hard to find.
 */
export function CategoryPicker({
  selected,
  onChange,
  canCreate,
}: {
  selected: string[];
  onChange: (categoryIds: string[]) => void;
  canCreate: boolean;
}) {
  const categories = useCategories();
  const create = useCreateCategory();
  const [name, setName] = useState('');
  const [type, setType] = useState<CategoryType>('Theme');

  if (categories.isPending) return <p className="text-sm text-muted-foreground">Loading categories…</p>;
  if (categories.isError) {
    return <p className="text-sm text-destructive">{describeError(categories.error).title}</p>;
  }

  function toggle(id: string, on: boolean) {
    onChange(on ? [...selected, id] : selected.filter((chosen) => chosen !== id));
  }

  function add() {
    if (!name.trim()) return;
    create.mutate(
      { name, type },
      {
        onSuccess: (category) => {
          onChange([...selected, category.id]);
          setName('');
        },
      },
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {GROUPS.map((group) => {
        const options = categories.data.filter((category) => category.type === group.type);

        return (
          <fieldset key={group.type} className="flex flex-col gap-2">
            <legend className="mb-1 text-sm font-medium text-foreground">{group.label}</legend>
            {options.length === 0 ? (
              <p className="text-sm text-muted-foreground">None yet.</p>
            ) : (
              <div className="flex flex-wrap gap-2">
                {options.map((category) => {
                  const checked = selected.includes(category.id);

                  return (
                    <label
                      key={category.id}
                      className={cx(
                        'flex cursor-pointer items-center gap-2 rounded-full border px-3 py-1 text-sm',
                        checked
                          ? 'border-primary bg-primary-subtle text-primary'
                          : 'border-border text-foreground',
                      )}
                    >
                      <input
                        type="checkbox"
                        className="h-4 w-4 accent-primary"
                        checked={checked}
                        onChange={(event) => toggle(category.id, event.target.checked)}
                      />
                      {category.name}
                    </label>
                  );
                })}
              </div>
            )}
          </fieldset>
        );
      })}

      {canCreate ? (
        <div className="flex flex-wrap items-end gap-2">
          <div className="min-w-0 flex-1 basis-48">
            <Input
              label="New category or theme"
              value={name}
              onChange={(event) => setName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  add();
                }
              }}
              error={create.isError ? describeError(create.error).title : undefined}
            />
          </div>
          <div className="w-36">
            <Select label="Kind" value={type} onChange={(event) => setType(event.target.value as CategoryType)}>
              <option value="Theme">Theme</option>
              <option value="Category">Category</option>
            </Select>
          </div>
          <Button type="button" variant="outline" onClick={add} loading={create.isPending} disabled={!name.trim()}>
            Add
          </Button>
        </div>
      ) : null}
    </div>
  );
}
