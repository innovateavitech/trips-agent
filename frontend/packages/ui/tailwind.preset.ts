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
      border: 'hsl(var(--border))',
      input: 'hsl(var(--input))',
      ring: 'hsl(var(--ring))',

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

    // One family. Inter, self-hosted via @fontsource-variable/inter —
    // no Google Fonts request, which matters on a slow Nigerian connection.
    fontFamily: {
      sans: ['"Inter Variable"', 'Inter', 'system-ui', '-apple-system', 'sans-serif'],
      mono: ['"JetBrains Mono"', 'ui-monospace', 'SFMono-Regular', 'monospace'],
    },

    extend: {
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
