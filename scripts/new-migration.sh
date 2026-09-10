#!/usr/bin/env bash
#
# new-migration.sh — generate an EF Core migration in the right place, with the right namespace.
#
#   ./scripts/new-migration.sh AddAgencies
#
# Why a script rather than the raw `dotnet ef` command: migrations live in db/migrations/ at the
# repository root, outside the C# project. That needs two flags every single time, and getting
# either wrong produces migrations that compile but that EF Core cannot find at runtime — a
# failure that shows up as "relation does not exist" on someone else's machine, not on yours.
#
# Generating a migration does NOT touch the database. Applying it does:
#
#   dotnet run --project services/TripsAgent.Api -- migrate

set -euo pipefail

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; BOLD=$'\033[1m'; DIM=$'\033[2m'; NC=$'\033[0m'

PROJECT="services/TripsAgent.Infrastructure"
OUTPUT_DIR="../../db/migrations"
NAMESPACE="TripsAgent.Infrastructure.Migrations"

cd "$(dirname "$0")/.."

NAME="${1:-}"

if [ -z "$NAME" ]; then
  cat >&2 <<MSG

${RED}${BOLD}✖  Give the migration a name.${NC}

  ${GREEN}./scripts/new-migration.sh AddAgencies${NC}

Name it after what it does, in PascalCase. ${DIM}AddAgencies${NC}, ${DIM}AddWalletHoldExpiry${NC},
${DIM}MakeSlugUnique${NC} — never ${DIM}Update1${NC}. Six months from now the name is the only
description anyone has.

MSG
  exit 1
fi

if ! printf '%s' "$NAME" | grep -qE '^[A-Z][A-Za-z0-9]*$'; then
  printf '%s\n' "${RED}✖  '${NAME}' is not a valid migration name.${NC}" >&2
  printf '%s\n' "   Use PascalCase letters and digits only, e.g. ${GREEN}AddAgencies${NC}." >&2
  exit 1
fi

if ! dotnet ef --version >/dev/null 2>&1; then
  printf '%s\n' "${DIM}Installing the dotnet-ef tool…${NC}"
  dotnet tool install --global dotnet-ef >/dev/null
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

printf '%s\n' "${DIM}Generating ${NAME} in db/migrations/ …${NC}"

dotnet ef migrations add "$NAME" \
  --project "$PROJECT" \
  --output-dir "$OUTPUT_DIR" \
  --namespace "$NAMESPACE"

cat <<MSG

${GREEN}${BOLD}✔  Migration created.${NC}

${BOLD}Now do these three things, in order:${NC}

  1. ${BOLD}Read the generated SQL.${NC}  ${DIM}db/migrations/*_${NAME}.cs${NC}
     EF guesses. Check it is dropping and creating what you actually meant — a rename it
     did not recognise looks like a drop plus an add, and that deletes live data.

  2. ${BOLD}Apply it locally.${NC}
     ${GREEN}dotnet run --project services/TripsAgent.Api -- migrate${NC}

  3. ${BOLD}Commit the whole db/migrations/ folder${NC}, including the ModelSnapshot file.
     The snapshot is how the next migration knows where it started from. Leaving it out
     makes the following migration try to recreate everything.

MSG
