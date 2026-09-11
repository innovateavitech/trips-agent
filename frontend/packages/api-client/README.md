# `@trips/api-client`

The typed client every front end uses to call the .NET API.

## What is generated, and what is not

| File | Who writes it |
|---|---|
| `openapi.json` | **Generated** — exported from the running API by `pnpm generate:api` |
| `src/generated/schema.ts` | **Generated** — rendered from `openapi.json` by the same command |
| `src/index.ts` | Hand-written, and deliberately tiny: `createApiClient()` and type re-exports |
| `scripts/generate-schema.mjs` | Hand-written: the generator itself |

Never edit a generated file. To change a type, change the C# record in
`backend/services/TripsAgent.Contracts`, then from `frontend/`:

```bash
pnpm generate:api
```

That runs `scripts/generate-api.sh`, which:

1. Starts the API in-process (with PostgreSQL in Docker) and writes the OpenAPI document it serves
   to `openapi.json`. This is the backend test `OpenApiDocumentTests` in update mode.
2. Renders `src/generated/schema.ts` from it with [openapi-typescript](https://openapi-ts.dev).

Commit both files together.

## How staleness is caught

- **Backend** — `OpenApiDocumentTests` fails `dotnet test` when `openapi.json` differs from what
  the API serves. A C# contract changed without regenerating.
- **Frontend** — `src/schema-freshness.test.ts` fails `pnpm test` when `schema.ts` differs from
  what `openapi.json` generates. The generated file was hand-edited, or only half regenerated.

## Using it

```ts
import { createApiClient } from '@trips/api-client';

const api = createApiClient({ baseUrl: '', fetch: authFetch });

const { data, error } = await api.GET('/api/v1/auth/me');
```

Paths, request bodies and response types are all checked by the compiler. The `fetch` option is
where an app plugs in authentication — see `apps/agent-console/src/api/auth-fetch.ts`, which adds
the bearer token and refreshes it transparently on a 401.

## A quirk worth knowing

.NET 10 describes integers as `integer | string`, because ASP.NET accepts numbers sent as strings.
So a C# `int` arrives as `number | string` in these types. The API always *sends* a JSON number;
convert with `Number(...)` where you need arithmetic.
