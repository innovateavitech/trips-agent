// Storefront lint rules: the shared base, plus Next.js's own Core Web Vitals set.
//
// Three things worth knowing:
//
// 1. `next lint` is deprecated (removed in Next.js 16), so the lint script calls
//    the ESLint CLI directly instead.
// 2. eslint-config-next is still published in the old "eslintrc" format, so
//    FlatCompat translates it into a flat config. This is exactly what
//    create-next-app generates — it is the conventional shape, not a workaround.
// 3. The file is .mjs because this app is not `"type": "module"`; the extension
//    is what tells Node to read it as an ES module.
//
// The shared *base* is used here rather than the shared *React* config, because
// eslint-config-next already brings the Rules of Hooks with it. Loading the
// react-hooks plugin twice is an error in flat config.

import { FlatCompat } from '@eslint/eslintrc';
import base from '@trips/config/eslint.base';

const compat = new FlatCompat({ baseDirectory: import.meta.dirname });

const config = [
  ...base,
  ...compat.extends('next/core-web-vitals'),

  // Next.js generates these; they are not ours to lint.
  { ignores: ['.next/**', 'next-env.d.ts'] },

  // Tooling config files are *meant* to export an anonymous object — that is the
  // shape every one of these tools expects. The rule is aimed at application
  // modules, where a named export is easier to trace.
  {
    files: ['*.config.{js,mjs,ts}'],
    rules: { 'import/no-anonymous-default-export': 'off' },
  },
];

export default config;
