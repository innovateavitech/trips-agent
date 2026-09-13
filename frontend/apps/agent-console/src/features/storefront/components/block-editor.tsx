import { Button, Card, Input, Select, Textarea } from '@trips/ui';
import { BLOCK_LABELS, type BlockType } from '../site-blocks';
import type { SiteBlock } from '../storefront-queries';

/**
 * One section of a page, open for editing.
 *
 * Every text box here is plain text and stays plain text all the way to the traveller's browser: an
 * agent's copy is shown on their own public domain to their own customers, and text that can never
 * be markup can never carry a script to them.
 */
export function BlockEditor({
  block,
  index,
  count,
  onChange,
  onMove,
  onRemove,
}: {
  block: SiteBlock;
  index: number;
  count: number;
  onChange: (block: SiteBlock) => void;
  onMove: (to: number) => void;
  onRemove: () => void;
}) {
  const label = BLOCK_LABELS[block.type as BlockType] ?? {
    name: block.type,
    description: 'A section this version of the console does not know how to edit.',
  };

  return (
    <Card className="p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 className="font-medium text-foreground">{label.name}</h3>
          <p className="text-sm text-muted-foreground">{label.description}</p>
        </div>

        <div className="flex items-center gap-1">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onMove(index - 1)}
            disabled={index === 0}
            aria-label={`Move ${label.name} up`}
          >
            Up
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onMove(index + 1)}
            disabled={index === count - 1}
            aria-label={`Move ${label.name} down`}
          >
            Down
          </Button>
          <Button size="sm" variant="ghost" onClick={onRemove} aria-label={`Remove ${label.name}`}>
            Remove
          </Button>
        </div>
      </div>

      <div className="mt-4 grid gap-4 md:grid-cols-2">
        {block.hero ? (
          <>
            <Input
              label="Heading"
              value={block.hero.heading}
              onChange={(event) =>
                onChange({ ...block, hero: { ...block.hero!, heading: event.target.value } })
              }
            />
            <Input
              label="Line underneath"
              value={block.hero.subheading ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  hero: { ...block.hero!, subheading: blankToNull(event.target.value) },
                })
              }
            />
            <Input
              label="Button text"
              hint="Leave both button boxes empty for a banner with no button."
              value={block.hero.ctaLabel ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  hero: { ...block.hero!, ctaLabel: blankToNull(event.target.value) },
                })
              }
            />
            <Input
              label="Button goes to"
              hint="A page on your site, such as /contact."
              value={block.hero.ctaHref ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  hero: { ...block.hero!, ctaHref: blankToNull(event.target.value) },
                })
              }
            />
          </>
        ) : null}

        {block.text ? (
          <>
            <Input
              label="Heading"
              value={block.text.heading ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  text: { ...block.text!, heading: blankToNull(event.target.value) },
                })
              }
            />
            <div className="md:col-span-2">
              <Textarea
                label="Words"
                hint="Leave a blank line between paragraphs."
                rows={6}
                value={block.text.body}
                onChange={(event) =>
                  onChange({ ...block, text: { ...block.text!, body: event.target.value } })
                }
              />
            </div>
          </>
        ) : null}

        {block.productGrid ? (
          <>
            <Input
              label="Heading"
              value={block.productGrid.heading}
              onChange={(event) =>
                onChange({
                  ...block,
                  productGrid: { ...block.productGrid!, heading: event.target.value },
                })
              }
            />
            <Select
              label="Show"
              value={block.productGrid.productType ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  productGrid: {
                    ...block.productGrid!,
                    productType: event.target.value === '' ? null : event.target.value,
                  },
                })
              }
            >
              <option value="">Everything I sell</option>
              <option value="Tour">Tours only</option>
              <option value="Package">Packages only</option>
              <option value="Visa">Visas only</option>
            </Select>
            <Input
              label="How many"
              type="number"
              min={1}
              max={12}
              value={String(block.productGrid.limit)}
              onChange={(event) =>
                onChange({
                  ...block,
                  productGrid: {
                    ...block.productGrid!,
                    limit: clamp(Number(event.target.value), 1, 12),
                  },
                })
              }
            />
            <p className="self-end text-sm text-muted-foreground md:col-span-2">
              The newest published products appear here on their own. Anything you unpublish drops
              out, and prices always come from your catalogue — this section never holds one.
            </p>
          </>
        ) : null}

        {block.contact ? (
          <>
            <Input
              label="Heading"
              value={block.contact.heading}
              onChange={(event) =>
                onChange({ ...block, contact: { ...block.contact!, heading: event.target.value } })
              }
            />
            <Input
              label="Line underneath"
              value={block.contact.intro ?? ''}
              onChange={(event) =>
                onChange({
                  ...block,
                  contact: { ...block.contact!, intro: blankToNull(event.target.value) },
                })
              }
            />
            <fieldset className="md:col-span-2">
              <legend className="mb-2 text-sm font-medium text-foreground">What to show</legend>
              <div className="flex flex-wrap gap-4">
                <Toggle
                  label="Email"
                  checked={block.contact.showEmail}
                  onChange={(showEmail) =>
                    onChange({ ...block, contact: { ...block.contact!, showEmail } })
                  }
                />
                <Toggle
                  label="Phone"
                  checked={block.contact.showPhone}
                  onChange={(showPhone) =>
                    onChange({ ...block, contact: { ...block.contact!, showPhone } })
                  }
                />
                <Toggle
                  label="WhatsApp"
                  checked={block.contact.showWhatsApp}
                  onChange={(showWhatsApp) =>
                    onChange({ ...block, contact: { ...block.contact!, showWhatsApp } })
                  }
                />
                <Toggle
                  label="Address"
                  checked={block.contact.showAddress}
                  onChange={(showAddress) =>
                    onChange({ ...block, contact: { ...block.contact!, showAddress } })
                  }
                />
              </div>
              <p className="mt-2 text-sm text-muted-foreground">
                The details themselves come from your website settings, so they are written once and
                shown everywhere.
              </p>
            </fieldset>
          </>
        ) : null}
      </div>
    </Card>
  );
}

function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-2 text-sm text-foreground">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="h-4 w-4 rounded border-input text-primary focus-visible:ring-2 focus-visible:ring-ring"
      />
      {label}
    </label>
  );
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : min;
}
