import { describe, expect, it } from 'vitest';
import { readCommittedSchema, renderSchema } from '../scripts/generate-schema.mjs';

/**
 * Two halves of one guarantee.
 *
 * The backend's OpenApiDocumentTests fails when openapi.json no longer matches
 * the API. This fails when src/generated/schema.ts no longer matches
 * openapi.json — someone hand-edited the generated file, or refreshed the
 * document and forgot to regenerate. Together they mean the types the console
 * compiles against are the API that is actually running.
 */
describe('the generated schema', () => {
  it('is exactly what openapi.json generates today', async () => {
    const committed = await readCommittedSchema();

    expect(
      committed,
      'src/generated/schema.ts is missing — run `pnpm generate:api`',
    ).not.toBeNull();
    expect(committed, 'src/generated/schema.ts is stale — run `pnpm generate:api`').toBe(
      await renderSchema(),
    );
  });
});
