import type { SiteBlock } from './storefront-queries';

/**
 * The four kinds of section a page is built from (docs/BUILD_PLAN.md, F4), and how the editor makes
 * an empty one.
 *
 * A block carries one settings object, named by its type. Keeping the shapes here rather than
 * scattered through the editor means adding a fifth kind later is one list and one form, and a
 * misspelled field is a compile error rather than a section that silently renders blank.
 */

export const BLOCK_TYPES = ['Hero', 'ProductGrid', 'Text', 'Contact'] as const;

export type BlockType = (typeof BLOCK_TYPES)[number];

/** What each kind is for, in the words an agent would use. */
export const BLOCK_LABELS: Record<BlockType, { name: string; description: string }> = {
  Hero: {
    name: 'Banner',
    description: 'A big heading over a picture, at the top of the page.',
  },
  ProductGrid: {
    name: 'What you sell',
    description: 'A grid of your tours, packages or visas. Prices come from your catalogue.',
  },
  Text: {
    name: 'Words',
    description: 'A heading and some paragraphs.',
  },
  Contact: {
    name: 'Get in touch',
    description: 'Your contact details, from the ones you saved on your website settings.',
  },
};

/** An empty block of one kind, ready for the agent to fill in. */
export function emptyBlock(type: BlockType): SiteBlock {
  switch (type) {
    case 'Hero':
      return {
        type,
        hero: { heading: '', subheading: null, imageAssetId: null, ctaLabel: null, ctaHref: null },
        productGrid: null,
        text: null,
        contact: null,
      };
    case 'ProductGrid':
      return {
        type,
        hero: null,
        productGrid: {
          heading: 'What we offer',
          mode: 'Latest',
          productType: null,
          productIds: [],
          limit: 6,
        },
        text: null,
        contact: null,
      };
    case 'Text':
      return {
        type,
        hero: null,
        productGrid: null,
        text: { heading: null, body: '' },
        contact: null,
      };
    case 'Contact':
      return {
        type,
        hero: null,
        productGrid: null,
        text: null,
        contact: {
          heading: 'Get in touch',
          intro: null,
          showEmail: true,
          showPhone: true,
          showWhatsApp: true,
          showAddress: true,
        },
      };
  }
}

/** Moves the block at `from` to `to`, leaving the list otherwise in order. */
export function move<T>(items: readonly T[], from: number, to: number): T[] {
  if (to < 0 || to >= items.length || from === to) {
    return [...items];
  }

  const next = [...items];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved!);

  return next;
}
