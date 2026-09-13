import { describe, expect, it } from 'vitest';
import { BLOCK_TYPES, emptyBlock, move } from '../site-blocks';

/**
 * The editor's own arithmetic. Both of these are easy to get subtly wrong and impossible to notice
 * from a screenshot: a block whose settings object is the wrong one renders blank on the live site,
 * and a reorder that drops a block loses an agent's work.
 */

describe('emptyBlock', () => {
  it('fills in exactly the settings its type names', () => {
    for (const type of BLOCK_TYPES) {
      const block = emptyBlock(type);
      const filled = (['hero', 'productGrid', 'text', 'contact'] as const).filter(
        (key) => block[key] !== null,
      );

      expect(block.type).toBe(type);
      expect(filled).toHaveLength(1);
    }
  });

  it('starts a product grid with what an agent most often wants', () => {
    const grid = emptyBlock('ProductGrid').productGrid!;

    // The newest published products, everything they sell, a tidy two rows of three.
    expect(grid.mode).toBe('Latest');
    expect(grid.productType).toBeNull();
    expect(grid.limit).toBe(6);
  });
});

describe('move', () => {
  it('moves one item and keeps the rest in order', () => {
    expect(move(['a', 'b', 'c', 'd'], 0, 2)).toEqual(['b', 'c', 'a', 'd']);
    expect(move(['a', 'b', 'c', 'd'], 3, 1)).toEqual(['a', 'd', 'b', 'c']);
  });

  it('never loses or duplicates anything', () => {
    const items = ['a', 'b', 'c', 'd', 'e'];

    for (let from = 0; from < items.length; from += 1) {
      for (let to = 0; to < items.length; to += 1) {
        expect([...move(items, from, to)].sort()).toEqual([...items].sort());
      }
    }
  });

  it('leaves the list alone when the move goes nowhere', () => {
    // The up button on the first block and the down button on the last are disabled, but a keyboard
    // or a double click can still get here.
    expect(move(['a', 'b'], 0, -1)).toEqual(['a', 'b']);
    expect(move(['a', 'b'], 1, 2)).toEqual(['a', 'b']);
    expect(move(['a', 'b'], 1, 1)).toEqual(['a', 'b']);
  });
});
