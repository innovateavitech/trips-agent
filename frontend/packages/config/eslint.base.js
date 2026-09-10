// Shared ESLint rules for every TypeScript package in the monorepo.
//
// This is the "flat config" format that ESLint 9 uses: a plain array of config
// objects, applied in order, where later objects override earlier ones.
//
// Deliberately NOT type-aware. Type-aware linting needs a full TypeScript
// program per file and roughly triples CI time; `pnpm typecheck` already runs
// `tsc` over every package, so the types are covered there instead.
//
// Add a rule here and it applies everywhere. That is the point — please don't
// disable rules file-by-file without saying why in a comment.

import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  // Never lint build output or dependencies.
  {
    ignores: ['dist/**', '.next/**', '.turbo/**', 'coverage/**', 'node_modules/**'],
  },

  js.configs.recommended,
  ...tseslint.configs.recommended,

  {
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: { ...globals.browser, ...globals.es2022 },
    },
    rules: {
      // An unused variable is usually a half-finished edit. Allow a leading
      // underscore for the ones that are deliberate, e.g. an unused callback arg.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' },
      ],

      // CLAUDE.md: "No commented-out code, no Console.WriteLine / console.log".
      // console.warn and console.error are real error reporting, so they stay.
      'no-console': ['error', { allow: ['warn', 'error'] }],

      // `any` switches type checking off for everything it touches. Warn rather
      // than error so it can be used knowingly — but --max-warnings 0 in CI
      // means you still have to justify it with an eslint-disable comment.
      '@typescript-eslint/no-explicit-any': 'warn',
    },
  },

  // Config files run in Node, not the browser.
  {
    files: ['*.config.{js,ts,mjs}', '*.config.*.{js,ts,mjs}'],
    languageOptions: { globals: globals.node },
  },
);
