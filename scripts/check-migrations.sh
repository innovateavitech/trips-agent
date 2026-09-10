#!/usr/bin/env bash
#
# check-migrations.sh — fails if the EF Core model has changed without a migration.
#
# The failure this prevents: you edit an entity, the app still works locally because your own
# database already has the column from an earlier experiment, and the change reaches main with
# no migration behind it. The next person's database has no such column and nothing starts.
#
# Run it yourself before pushing:   ./scripts/check-migrations.sh
# CI runs it on every pull request: .github/workflows/migrations.yml
#
# No database is needed. EF builds the model in memory and compares it against the snapshot
# the last migration left behind; it never opens a connection to do that.

set -euo pipefail

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[0;33m'
BOLD=$'\033[1m'; DIM=$'\033[2m'; NC=$'\033[0m'

PROJECT="services/TripsAgent.Infrastructure"

cd "$(dirname "$0")/.."

if ! dotnet ef --version >/dev/null 2>&1; then
  printf '%s\n' "${DIM}Installing the dotnet-ef tool…${NC}"
  dotnet tool install --global dotnet-ef >/dev/null
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

# How many migrations exist. Purely informational, but it turns a confusing green tick on a
# brand-new project into something a reader can understand.
migration_count="$(
  dotnet ef migrations list --project "$PROJECT" --no-connect --prefix-output 2>/dev/null \
    | grep -c '^data:' || true
)"

# `has-pending-model-changes` exits 0 when the model matches the last migration, and non-zero
# when it does not — but it also exits non-zero if it could not run at all (a compile error, a
# DbContext it cannot construct). Those are very different problems, so tell them apart rather
# than blaming a missing migration for a broken build.
set +e
output="$(dotnet ef migrations has-pending-model-changes --project "$PROJECT" 2>&1)"
status=$?
set -e

if [ "$status" -eq 0 ]; then
  if [ "$migration_count" -eq 0 ]; then
    printf '%s\n' "${GREEN}✔  Model and migrations agree.${NC} ${DIM}(no migrations yet, and no entities to migrate)${NC}"
    printf '%s\n' "${DIM}   The first migration arrives with the first entity — agencies, issue #10.${NC}"
  else
    printf '%s\n' "${GREEN}✔  Model and migrations agree.${NC} ${DIM}(${migration_count} migration(s))${NC}"
  fi
  exit 0
fi

# EF's wording for "you changed the model and did not write a migration".
if printf '%s' "$output" | grep -qiE 'changes have been made to the model|pending model changes'; then
  cat >&2 <<MSG

${RED}${BOLD}✖  The model has changes with no migration.${NC}

You changed an entity or its configuration, but no migration describes the change, so the
database schema and the C# model no longer agree.

${BOLD}Fix — generate one and commit it:${NC}

  ${GREEN}dotnet ef migrations add DescribeYourChange --project ${PROJECT}${NC}

Name it after what it does: ${DIM}AddAgencies${NC}, ${DIM}AddWalletHoldExpiry${NC} — not ${DIM}Update1${NC}.

Then commit the generated files under ${DIM}${PROJECT}/Migrations/${NC} alongside your change.

MSG
  exit 1
fi

cat >&2 <<MSG

${YELLOW}${BOLD}✖  The migration check could not run.${NC}

This is ${BOLD}not${NC} a missing migration — EF Core could not build the model at all. Usually that
means the solution does not compile, or a convention rejected an entity mapping.

${BOLD}What EF reported:${NC}

$output

${BOLD}Try:${NC}  ${GREEN}dotnet build TripsAgent.slnx${NC}  ${DIM}— fix the build first, then re-run this script.${NC}

MSG
exit "$status"
