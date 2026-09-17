import plugin from 'tailwindcss/plugin';
import type { Config } from 'tailwindcss';

/**
 * Shared Tailwind preset. Every app extends THIS — never redefines colours.
 *
 * Colours map to the CSS variables in src/styles/tokens.css, so a token change
 * propagates everywhere at once. There is deliberately no way to reach a colour
 * that is not a token: `bg-[#325DEC]` fails lint, and the palette below is a
 * replacement, not an extension, so Tailwind's default 250-colour palette
 * (bg-blue-500, text-slate-700, …) is not available.
 *
 * That is the whole anti-drift mechanism. Please do not "temporarily" widen it.
 */
export const preset = {
  darkMode: ['class'],
  content: [],
  theme: {
    // NOTE: `colors` (not `extend.colors`) — this REPLACES Tailwind's defaults.
    colors: {
      inherit: 'inherit',
      current: 'currentColor',
      transparent: 'transparent',
      white: '#FFFFFF',
      black: '#000000',

      background: 'hsl(var(--background))',
      foreground: 'hsl(var(--foreground))',
      border: {
        DEFAULT: 'hsl(var(--border))',
        subtle: 'hsl(var(--border-subtle))',
      },
      input: 'hsl(var(--input))',
      ring: 'hsl(var(--ring))',
      chart: {
        1: 'hsl(var(--chart-1))',
        2: 'hsl(var(--chart-2))',
      },
      'progress-track': 'hsl(var(--progress-track))',

      primary: {
        DEFAULT: 'hsl(var(--primary))',
        foreground: 'hsl(var(--primary-foreground))',
        hover: 'hsl(var(--primary-hover))',
        active: 'hsl(var(--primary-active))',
        subtle: 'hsl(var(--primary-subtle))',
        border: 'hsl(var(--primary-border))',
      },
      secondary: {
        DEFAULT: 'hsl(var(--secondary))',
        foreground: 'hsl(var(--secondary-foreground))',
      },
      muted: {
        DEFAULT: 'hsl(var(--muted))',
        foreground: 'hsl(var(--muted-foreground))',
      },
      accent: {
        DEFAULT: 'hsl(var(--accent))',
        foreground: 'hsl(var(--accent-foreground))',
      },
      card: {
        DEFAULT: 'hsl(var(--card))',
        foreground: 'hsl(var(--card-foreground))',
      },
      popover: {
        DEFAULT: 'hsl(var(--popover))',
        foreground: 'hsl(var(--popover-foreground))',
      },
      destructive: {
        DEFAULT: 'hsl(var(--destructive))',
        foreground: 'hsl(var(--destructive-foreground))',
        subtle: 'hsl(var(--destructive-subtle))',
        'subtle-foreground': 'hsl(var(--destructive-subtle-foreground))',
      },
      success: {
        DEFAULT: 'hsl(var(--success))',
        foreground: 'hsl(var(--success-foreground))',
        subtle: 'hsl(var(--success-subtle))',
        'subtle-foreground': 'hsl(var(--success-subtle-foreground))',
      },
      warning: {
        DEFAULT: 'hsl(var(--warning))',
        foreground: 'hsl(var(--warning-foreground))',
        subtle: 'hsl(var(--warning-subtle))',
        'subtle-foreground': 'hsl(var(--warning-subtle-foreground))',
      },
      sidebar: {
        DEFAULT: 'hsl(var(--sidebar))',
        foreground: 'hsl(var(--sidebar-foreground))',
        'muted-foreground': 'hsl(var(--sidebar-muted-foreground))',
        accent: 'hsl(var(--sidebar-accent))',
        'accent-foreground': 'hsl(var(--sidebar-accent-foreground))',
        border: 'hsl(var(--sidebar-border))',
        primary: 'hsl(var(--sidebar-primary))',
      },
      info: {
        DEFAULT: 'hsl(var(--info))',
        foreground: 'hsl(var(--info-foreground))',
        subtle: 'hsl(var(--info-subtle))',
        'subtle-foreground': 'hsl(var(--info-subtle-foreground))',
      },
    },

    // Two families, both self-hosted (no Google Fonts/CDN request — matters
    // on a slow Nigerian connection). Plus Jakarta Sans carries everything:
    // headings, nav, buttons, body copy. Inter is kept for `font-numeric`
    // only — money figures, wallet balances, and similar dense numeric/meta
    // text, where its tabular figures earn their place over Jakarta's
    // proportional ones. Do not reach for `font-numeric` outside that use.
    fontFamily: {
      sans: [
        '"Plus Jakarta Sans Variable"',
        '"Plus Jakarta Sans"',
        'system-ui',
        '-apple-system',
        'sans-serif',
      ],
      numeric: ['"Inter Variable"', 'Inter', 'system-ui', '-apple-system', 'sans-serif'],
      mono: ['"JetBrains Mono"', 'ui-monospace', 'SFMono-Regular', 'monospace'],
    },

    extend: {
      // Below Tailwind's own `text-xs`, for the one place regular type is too
      // big: a numeric badge inside a 24px pill. Named, not an arbitrary
      // `text-[9px]` — the same anti-drift reasoning as the colour tokens.
      fontSize: {
        '2xs': ['0.6875rem', { lineHeight: '0.875rem' }],
      },
      // A pill-tab width that falls between Tailwind's own 24/28 steps.
      spacing: {
        25: '6.25rem', // 100px
      },
      // Fixed heights for the Home page's card grid, so a card's height comes
      // from the design, not from however much its own data happens to be —
      // content that runs long scrolls inside the card instead of stretching
      // it, which is what keeps two independent columns lining up.
      //
      // card-sm + card-md + the 10px gap between them (`gap-2.5`, a stock
      // Tailwind step) must sum to exactly card-lg, since the wallet-balance
      // and earnings cards stack to match one row on the other side. Change
      // any one of these three numbers and the other two must follow.
      height: {
        'card-sm': '11.75rem', // 188px — wallet balance
        'card-md': '12.125rem', // 194px — earnings
        'card-lg': '24.5rem', // 392px — upcoming items, for you today, transactions
      },
      borderRadius: {
        lg: 'var(--radius)',
        md: 'calc(var(--radius) - 2px)',
        sm: 'calc(var(--radius) - 4px)',
      },
      keyframes: {
        'fade-in': { from: { opacity: '0' }, to: { opacity: '1' } },
        'slide-up': {
          from: { opacity: '0', transform: 'translateY(4px)' },
          to: { opacity: '1', transform: 'translateY(0)' },
        },
        'slide-in-left': {
          from: { transform: 'translateX(-100%)' },
          to: { transform: 'translateX(0)' },
        },
      },
      animation: {
        // Short and few, on purpose. Motion earns its place or it is noise.
        'fade-in': 'fade-in 150ms ease-out',
        'slide-up': 'slide-up 200ms ease-out',
        // The tablet navigation drawer. Answers a tap, so it is allowed to move.
        'slide-in-left': 'slide-in-left 220ms cubic-bezier(0.16, 1, 0.3, 1)',
      },
    },
  },
  plugins: [
    plugin(({ addBase }) => {
      addBase({
        '*': { borderColor: 'hsl(var(--border))' },
        body: {
          backgroundColor: 'hsl(var(--background))',
          color: 'hsl(var(--foreground))',
          fontFeatureSettings: "'cv02', 'cv03', 'cv04', 'cv11'",
          WebkitFontSmoothing: 'antialiased',
        },
        // Every focusable thing gets the same visible ring. Do not remove it —
        // agents work at speed and many of them use the keyboard.
        ':focus-visible': {
          outline: '2px solid hsl(var(--ring))',
          outlineOffset: '2px',
        },
      });
    }),
  ],
} satisfies Partial<Config>;

export default preset;
