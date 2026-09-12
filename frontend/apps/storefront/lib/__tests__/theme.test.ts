import { describe, expect, it } from 'vitest';
import { hexToHsl, themeVariables } from '../theme';
import type { Site } from '../api';

/**
 * The agency's colour becomes the design system's own tokens (CLAUDE.md rules 4 and 7). What is
 * pinned here is that the conversion is right, and that a bad value in a database row leaves the
 * stylesheet's colours alone instead of taking the page down.
 */

describe('hexToHsl', () => {
  it('converts the brand blue to the channels the token holds', () => {
    // tokens.css states `226 83% 56%` for --primary; the extra tenth is the exact value, rounded
    // once rather than twice.
    expect(hexToHsl('#325DEC')).toEqual({ h: 226, s: 83, l: 56.1 });
  });

  it('accepts a short hex and a missing hash', () => {
    expect(hexToHsl('#fff')).toEqual({ h: 0, s: 0, l: 100 });
    expect(hexToHsl('000000')).toEqual({ h: 0, s: 0, l: 0 });
  });

  it('returns nothing for a value that is not a colour', () => {
    // These come from a row an agent filled in, so none of them may throw.
    for (const value of ['', '  ', 'blue', '#12345', '#gggggg', null, undefined]) {
      expect(hexToHsl(value)).toBeNull();
    }
  });
});

describe('themeVariables', () => {
  it('redefines the primary family and leaves everything else to the stylesheet', () => {
    const declarations = themeVariables(siteWith('#325DEC', null));

    expect(declarations).toContain('--primary: 226 83% 56.1%;');
    expect(declarations).toContain('--ring: 226 83% 56.1%;');

    // Warnings and errors have to stay legible as warnings and errors: choosing a brand colour is
    // not choosing what "destructive" looks like.
    expect(declarations).not.toContain('--destructive');
    expect(declarations).not.toContain('--background');
  });

  it('leaves the tokens untouched when the colour is unusable', () => {
    expect(themeVariables(siteWith('not a colour', null))).toBe('');
  });

  it('only takes an accent from a secondary colour that is one', () => {
    expect(themeVariables(siteWith('#325DEC', '#1F8A5B'))).toContain('--accent:');
    expect(themeVariables(siteWith('#325DEC', 'nonsense'))).not.toContain('--accent:');
  });
});

function siteWith(primaryColor: string, secondaryColor: string | null): Site {
  return {
    theme: { primaryColor, secondaryColor },
  } as Site;
}
