// Not covered by `tsc --noEmit`: `vitest/config` v2 ships Vite 5 types while this
// app runs Vite 6, so `react()` reads as a foreign Plugin type. The mismatch is
// types-only — vitest runs this file correctly — and bumping vitest to v3 to fix
// it would change shared tooling for every app in the workspace. Revisit in #7,
// which owns the CI/test setup.
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    // Component tests need a DOM. The http tests do not, but running everything
    // in one environment beats maintaining two configs for a handful of files.
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
