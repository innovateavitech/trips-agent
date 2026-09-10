#!/usr/bin/env bash
#
# EF Core migration helper.
#
# Every `dotnet ef` call needs the same --project/--startup-project pair, because the
# DbContext lives in Infrastructure while the host that configures it is the Api. Rather than
# expecting anyone to remember that, use this:
#
#   ./scripts/ef.sh add AddAgenciesTable   # create a new migration
#   ./scripts/ef.sh update                 # apply pending migrations to your local database
#   ./scripts/ef.sh list                   # show every migration and whether it is applied
#   ./scripts/ef.sh remove                 # delete the most recent migration (if not applied)
#   ./scripts/ef.sh check                  # fail if the model has changes with no migration
#   ./scripts/ef.sh script                 # print the full SQL, for review before a deploy
#
set -euo pipefail

# Everything below runs from backend/, where the .NET solution and the pinned dotnet-ef
# tool manifest live. That means you can call this script from anywhere in the repo.
SCRIPT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
REPO_ROOT="$(dirname "$(dirname "$SCRIPT_PATH")")"
cd "$REPO_ROOT/backend"

PROJECT="services/TripsAgent.Infrastructure"
STARTUP="services/TripsAgent.Api"
OUTPUT_DIR="Persistence/Migrations"

# dotnet-ef is pinned in .config/dotnet-tools.json so everyone runs the same version.
# `tool restore` is cheap once it is already installed.
dotnet tool restore >/dev/null

ef() {
  dotnet ef "$@" --project "$PROJECT" --startup-project "$STARTUP"
}

command="${1:-help}"
shift || true

case "$command" in
  add)
    if [[ $# -lt 1 ]]; then
      echo "error: a migration needs a name." >&2
      echo "usage: ./scripts/ef.sh add AddAgenciesTable" >&2
      exit 1
    fi
    ef migrations add "$1" --output-dir "$OUTPUT_DIR"
    echo
    echo "Migration written to backend/services/TripsAgent.Infrastructure/Persistence/Migrations/."
    echo "Read the generated Up() before committing —"
    echo "EF guesses, and a guessed column rename is a dropped column with data in it."
    ;;

  update)
    ef database update "$@"
    ;;

  list)
    ef migrations list
    ;;

  remove)
    ef migrations remove
    ;;

  check)
    # Exits non-zero when the model and the migrations have drifted apart. CI runs this so a
    # forgotten migration is caught in the pull request, not on the deploy.
    if ef migrations has-pending-model-changes; then
      echo "Model and migrations are in sync."
    else
      echo
      echo "The EF Core model has changes with no matching migration." >&2
      echo "Fix: ./scripts/ef.sh add <DescriptiveName>" >&2
      exit 1
    fi
    ;;

  script)
    ef migrations script --idempotent "$@"
    ;;

  help | --help | -h)
    sed -n '3,15p' "$SCRIPT_PATH" | sed 's/^# \{0,1\}//'
    ;;

  *)
    echo "error: unknown command '$command'" >&2
    echo "run ./scripts/ef.sh help" >&2
    exit 1
    ;;
esac
