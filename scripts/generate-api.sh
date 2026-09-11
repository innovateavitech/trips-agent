#!/usr/bin/env bash
#
# scripts/generate-api.sh — regenerate the frontend's typed API client.
#
# Run it through `pnpm generate:api` from frontend/, after changing anything in
# backend/services/TripsAgent.Contracts or an endpoint's signature.
#
#   1. Export the OpenAPI document the API actually serves, into
#      frontend/packages/api-client/openapi.json. This runs the backend test
#      OpenApiDocumentTests in update mode: it starts the API in-process against
#      a throwaway PostgreSQL in Docker, so Docker has to be running.
#   2. Render frontend/packages/api-client/src/generated/schema.ts from it.
#
# Commit both files. CI fails if either drifts from the other or from the API.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

printf '1/2  Exporting the OpenAPI document from the API (needs Docker)\n'
TRIPS_UPDATE_OPENAPI=1 dotnet test backend/tests/TripsAgent.IntegrationTests \
  --filter "FullyQualifiedName~TripsAgent.IntegrationTests.Api.OpenApiDocumentTests" \
  --nologo

printf '2/2  Generating TypeScript types\n'
node frontend/packages/api-client/scripts/generate-schema.mjs

printf 'Done. Commit openapi.json and src/generated/schema.ts together.\n'
