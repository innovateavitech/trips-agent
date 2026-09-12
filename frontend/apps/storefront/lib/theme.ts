import type { Site } from './api';

/**
 * Painting a site in the agency's colours (CLAUDE.md rule 4).
 *
 * The design system says a colour may only ever be a token, and that stands here: every component on
 * the storefront still says `bg-primary`, `text-muted-foreground`, `border-border`. What changes is
 * what those tokens *hold*. An agency's brand colour is not a value in our source — it is a value in
 * their branding record — so it arrives with the site and is written into the token variables for
 * that one page render.
 *
 * The tokens are stored as bare HSL channels (`226 83% 56%`) so Tailwind can compose them with an
 * alpha, which is why the agency's hex has to be converted rather than dropped in.
 */

/** The channels of one colour, in the shape a token holds. */
interface Hsl {
  h: number;
  s: number;
  l: number;
}

/**
 * `#325DEC` or `#35E` to its HSL channels. Null for anything that is not a hex colour — the value
 * comes from a database row, and a page must not fail to render because one is malformed.
 */
export function hexToHsl(hex: string | null | undefined): Hsl | null {
  if (!hex) {
    return null;
  }

  const value = hex.trim().replace(/^#/, '');

  const full =
    value.length === 3
      ? value
          .split('')
          .map((channel) => channel + channel)
          .join('')
      : value;

  if (!/^[0-9a-fA-F]{6}$/.test(full)) {
    return null;
  }

  const red = parseInt(full.slice(0, 2), 16) / 255;
  const green = parseInt(full.slice(2, 4), 16) / 255;
  const blue = parseInt(full.slice(4, 6), 16) / 255;

  const max = Math.max(red, green, blue);
  const min = Math.min(red, green, blue);
  const lightness = (max + min) / 2;
  const delta = max - min;

  if (delta === 0) {
    return { h: 0, s: 0, l: round(lightness * 100) };
  }

  const saturation = delta / (1 - Math.abs(2 * lightness - 1));

  let hue: number;

  if (max === red) {
    hue = ((green - blue) / delta) % 6;
  } else if (max === green) {
    hue = (blue - red) / delta + 2;
  } else {
    hue = (red - green) / delta + 4;
  }

  hue = Math.round(hue * 60);

  return {
    h: hue < 0 ? hue + 360 : hue,
    s: round(saturation * 100),
    l: round(lightness * 100),
  };
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

/** The same hue, pushed towards black or white — for hover, active and subtle shades. */
function shift(colour: Hsl, lightness: number): Hsl {
  return { ...colour, l: Math.min(100, Math.max(0, round(lightness))) };
}

function channels(colour: Hsl): string {
  return `${colour.h} ${colour.s}% ${colour.l}%`;
}

/**
 * The token overrides for one site, as a CSS declaration block.
 *
 * Only the primary family is redefined. Everything else — surfaces, text, borders, the states that
 * have to stay legible as warnings and errors — keeps the values in `tokens.css`, because an agency
 * choosing a brand colour is not choosing what "destructive" looks like.
 *
 * Returns an empty string when the agency has no usable colour, which leaves the tokens exactly as
 * the stylesheet defined them.
 */
export function themeVariables(site: Site): string {
  const primary = hexToHsl(site.theme.primaryColor);

  if (!primary) {
    return '';
  }

  const declarations = [
    ['--primary', channels(primary)],
    ['--primary-hover', channels(shift(primary, primary.l - 9))],
    ['--primary-active', channels(shift(primary, primary.l - 18))],
    ['--primary-subtle', channels({ ...primary, l: 97 })],
    ['--primary-border', channels({ ...primary, l: 86 })],
    ['--ring', channels(primary)],
    // The header and footer sit on the brand colour, so the sidebar family follows it too.
    ['--sidebar', channels(shift(primary, Math.min(primary.l, 16)))],
  ];

  const secondary = hexToHsl(site.theme.secondaryColor);

  if (secondary) {
    declarations.push(['--accent', channels({ ...secondary, l: 96 })]);
    declarations.push(['--accent-foreground', channels(shift(secondary, 20))]);
  }

  return declarations.map(([name, value]) => `${name}: ${value};`).join(' ');
}
