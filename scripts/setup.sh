#!/usr/bin/env bash
#
# scripts/setup.sh — run this once after cloning.
#
#   ./scripts/setup.sh
#
# Installs the shared git hooks and checks you have the tools you need.
# Safe to re-run at any time.

set -uo pipefail

RED=$'\033[0;31m'; YELLOW=$'\033[0;33m'; GREEN=$'\033[0;32m'
BLUE=$'\033[0;34m'; BOLD=$'\033[1m'; DIM=$'\033[2m'; NC=$'\033[0m'

cd "$(git rev-parse --show-toplevel 2>/dev/null)" || {
  printf '%s\n' "${RED}Not inside a git repository.${NC}"; exit 1; }

printf '\n%s\n' "${BOLD}${BLUE}Trips Agent Platform — developer setup${NC}"
printf '%s\n\n' "${DIM}$(pwd)${NC}"

# ------------------------------------------------------------------ hooks
printf '%s\n' "${BOLD}Git hooks${NC}"
git config core.hooksPath .githooks
chmod +x .githooks/* 2>/dev/null || true
printf '  %s\n' "${GREEN}✓${NC} core.hooksPath → .githooks"
printf '  %s\n' "${GREEN}✓${NC} pre-commit   ${DIM}blocks commits on main, secrets, huge files${NC}"
printf '  %s\n' "${GREEN}✓${NC} commit-msg   ${DIM}enforces Conventional Commits${NC}"
printf '  %s\n\n' "${GREEN}✓${NC} pre-push     ${DIM}blocks pushes to main${NC}"

# --------------------------------------------------------------- ai skills
printf '%s\n' "${BOLD}Design skills${NC}"
if [ "${SKIP_SKILLS:-0}" = "1" ]; then
  printf '  %s\n\n' "${YELLOW}!${NC} skipped (SKIP_SKILLS=1)"
elif command -v npx >/dev/null 2>&1; then
  # Impeccable ships a ~12MB platform-specific binary, so it is installed per
  # developer rather than committed. Both hooks no-op if it is missing.
  npx --yes impeccable@4 install --project --providers=claude -y >/dev/null 2>&1 \
    && printf '  %s\n' "${GREEN}✓${NC} impeccable   ${DIM}23 commands + 61 drift detectors${NC}" \
    || printf '  %s\n' "${YELLOW}!${NC} impeccable   ${DIM}install failed — run manually${NC}"
  printf '  %s\n' "${GREEN}✓${NC} frontend-design, emil-design-eng ${DIM}(committed in .agents/skills)${NC}"
  printf '\n'
else
  printf '  %s\n\n' "${YELLOW}!${NC} npx not found — install Node first"
fi

# ------------------------------------------------------------- git identity
printf '%s\n' "${BOLD}Git identity${NC}"
name=$(git config --get user.name || true)
email=$(git config --get user.email || true)
if [ -n "$name" ] && [ -n "$email" ]; then
  printf '  %s\n\n' "${GREEN}✓${NC} $name <$email>"
else
  printf '  %s\n' "${YELLOW}!${NC} Not set. Run:"
  printf '      %s\n' "${DIM}git config --global user.name  \"Your Name\"${NC}"
  printf '      %s\n\n' "${DIM}git config --global user.email \"you@example.com\"${NC}"
fi

# ----------------------------------------------------------------- tooling
printf '%s\n' "${BOLD}Required tools${NC}"
missing=0
check() { # name  command  install-hint
  if command -v "$2" >/dev/null 2>&1; then
    v=$($2 --version 2>&1 | head -1 | tr -d '\n')
    printf '  %s %-10s %s\n' "${GREEN}✓${NC}" "$1" "${DIM}${v}${NC}"
  else
    printf '  %s %-10s %s\n' "${RED}✗${NC}" "$1" "${YELLOW}$3${NC}"
    missing=1
  fi
}
check "git"    git    "install git"
check "gh"     gh     "brew install gh"
check "dotnet" dotnet "https://dotnet.microsoft.com/download"
check "node"   node   "brew install node"
check "pnpm"   pnpm   "npm install -g pnpm"
check "docker" docker "install Docker Desktop"
printf '\n'

if command -v docker >/dev/null 2>&1; then
  if docker info >/dev/null 2>&1; then
    printf '  %s\n\n' "${GREEN}✓${NC} Docker daemon is running"
  else
    printf '  %s\n\n' "${YELLOW}!${NC} Docker is installed but not running — start Docker Desktop"
  fi
fi

# -------------------------------------------------------------------- .env
if [ -f .env.example ] && [ ! -f .env ]; then
  cp .env.example .env
  printf '%s\n' "${GREEN}✓${NC} Created .env from .env.example ${DIM}— fill in the secrets${NC}"
  printf '\n'
fi

# ------------------------------------------------------------------- done
printf '%s\n' "${BOLD}${GREEN}Setup complete.${NC}"
[ "$missing" -eq 1 ] && printf '%s\n' "${YELLOW}Install the missing tools above before you start.${NC}"

cat <<EOF

${BOLD}Next${NC}
  1. Read ${BLUE}README.md${NC}         ${DIM}what we're building${NC}
  2. Read ${BLUE}CONTRIBUTING.md${NC}   ${DIM}how we work${NC}
  3. Read ${BLUE}docs/onboarding.md${NC} ${DIM}your first week${NC}

${BOLD}Where to run things${NC}
  ${DIM}cd backend  && dotnet build     the .NET solution lives here
  cd frontend && pnpm install     the pnpm workspace lives here
  ./scripts/check-design.sh       repo-root scripts work from anywhere${NC}

${BOLD}Starting a task${NC}
  ${DIM}git checkout main && git pull origin main
  git checkout -b feat/M1-my-task${NC}

${BOLD}The one rule${NC}
  Nobody pushes to main. The hooks will stop you if you try.

EOF
