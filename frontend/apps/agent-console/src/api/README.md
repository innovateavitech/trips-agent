# `src/api` — how this app talks to the backend

## The generated client is not here yet

Issue #48 asks for "generated API client from OpenAPI wired up". It is **not** wired up,
because there is nothing to generate from:

- `pnpm generate:api` is still a stub — `echo 'TODO: … see issue #7'`
- `packages/api-client/` does not exist
- no endpoint in `services/` is implemented; `TripsAgent.Application` and `.Domain` contain
  only `AssemblyMarker.cs`

Hand-writing `packages/api-client/` was not an option — CLAUDE.md marks it **GENERATED, never
hand-edit**, and a hand-written file there would be silently destroyed the first time anyone
runs the generator.

## What is here instead

A transport layer the generated client will sit *on top of*, not compete with:

| File | Responsibility |
|---|---|
| `config.ts` | Base URL, from `VITE_API_BASE_URL` |
| `tokens.ts` | The access token, in memory only |
| `refresh.ts` | The refresh contract — **the one file #16 changes** |
| `http.ts` | `fetch` wrapper: auth header, JSON, errors, transparent 401 refresh |
| `query-client.ts` | TanStack Query defaults |

## Wiring the generated client up, when it lands

Most OpenAPI generators accept a custom fetch function. Point it at `http()` and every
generated call inherits the auth header, the 401 refresh and the `ApiError` shape for free:

```ts
import { http } from './http';

export const api = createClient({ fetcher: http });
```

Then delete `src/auth/types.ts` and import the generated `MeResponse` instead — those types
are hand-written **only** because `/auth/me` has no generated equivalent yet.
