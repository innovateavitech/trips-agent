// The shared base rules. The generated schema is excluded: it is machine output,
// rewritten wholesale by `pnpm generate:api`, so a lint finding in it could only
// be "fixed" by an edit the next regeneration throws away.
import base from '@trips/config/eslint.base';

export default [{ ignores: ['src/generated/**'] }, ...base];
