import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

/* Unmount between tests. Without it, a guard from an earlier test is still
   mounted and its redirect fires during the next one. */
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
