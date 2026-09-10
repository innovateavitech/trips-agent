import type { Config } from 'tailwindcss';
import { preset } from '@trips/ui/tailwind.preset';

/**
 * Extends the shared preset. Do NOT redefine colours or fonts here —
 * they live in packages/ui/tailwind.preset.ts so every app stays identical.
 * `pnpm check:design` fails if you try.
 */
export default {
  presets: [preset],
  content: [
    './index.html',
    './src/**/*.{ts,tsx}',
    '../../packages/ui/src/**/*.{ts,tsx}',
  ],
} satisfies Config;
