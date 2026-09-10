// Shared ESLint rules for the React front ends.
//
// Everything in eslint.base.js, plus the Rules of Hooks. Those two rules catch a
// class of bug that is invisible in review and very confusing at runtime: a hook
// called conditionally, or a dependency array that lies about what an effect uses.

import reactHooks from 'eslint-plugin-react-hooks';

import base from './eslint.base.js';

export default [
  ...base,
  {
    files: ['**/*.{ts,tsx}'],
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // Not a style preference: a wrong dependency array is a stale-closure bug.
      'react-hooks/exhaustive-deps': 'error',
    },
  },
];
