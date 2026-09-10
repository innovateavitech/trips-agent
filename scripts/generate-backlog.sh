#!/usr/bin/env bash
#
# Regenerates docs/BACKLOG.md from the live GitHub issues.
# Run after adding, retitling, closing or re-scoping issues.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

command -v gh >/dev/null 2>&1 || { echo "gh CLI not found — brew install gh"; exit 1; }

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

echo "Fetching issues..."
gh issue list --state all --limit 200 \
  --json number,title,state,labels,milestone,body > "$tmp"

python3 scripts/generate-backlog.py "$tmp"
echo "Done. Review the diff, then commit."
