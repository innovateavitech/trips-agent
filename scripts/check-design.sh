#!/usr/bin/env bash
#
# scripts/check-design.sh — deterministic design-drift detector.
#
# Runs in CI and from `pnpm check:design`. It answers one question:
# "has anyone introduced a colour, font or spacing value that is not a token?"
#
# Drift is not a style opinion. The moment two blues exist, nobody knows which
# is correct, and every later screen copies whichever one it saw last.

set -uo pipefail
cd "$(git rev-parse --show-toplevel)"

RED=$'\033[0;31m'; YELLOW=$'\033[0;33m'; GREEN=$'\033[0;32m'
BOLD=$'\033[1m'; DIM=$'\033[2m'; NC=$'\033[0m'

FAILED=0
BRAND_PRIMARY_HSL="226 83% 56%"
TOKENS="frontend/packages/ui/src/styles/tokens.css"
PRESET="frontend/packages/ui/tailwind.preset.ts"

# Files allowed to contain raw colour values.
is_allowed() {
  case "$1" in
    "$TOKENS"|"$PRESET") return 0 ;;
    *) return 1 ;;
  esac
}

# Everything we lint: app and package source only.
sources() {
  # Directory pathspecs, not globs: `git ls-files 'frontend/apps/**/*.tsx'` silently
  # misses nested paths, which would let drift through unnoticed.
  # --others --exclude-standard includes new files that are not yet committed.
  git ls-files --cached --others --exclude-standard -- frontend/apps frontend/packages 2>/dev/null \
    | grep -E '\.(tsx|ts|css)$' \
    | grep -v '/generated/' \
    | grep -v 'node_modules' \
    | sort -u || true
}

fail() {
  printf '\n%s\n' "${RED}${BOLD}✖  $1${NC}" >&2
  printf '%s\n' "$2" >&2
  FAILED=1
}

printf '\n%s\n' "${BOLD}Design token check${NC}"

# ---------------------------------------------------------------- 1. hex colours
hits=""
while IFS= read -r f; do
  is_allowed "$f" && continue
  # The second grep drops comments and false positives such as `#region`. It has to allow for
  # the `12:` prefix `grep -n` adds, or the anchors can never match a commented-out colour.
  found=$(grep -nE '#[0-9a-fA-F]{3,8}\b' "$f" 2>/dev/null | grep -vE '^[0-9]+:[[:space:]]*(//|\*)|#[0-9a-fA-F]*[g-zG-Z]' || true)
  [ -n "$found" ] && hits+="  ${f}\n$(echo "$found" | sed 's/^/      /')\n"
done < <(sources)
if [ -n "$hits" ]; then
  fail "Hard-coded hex colour outside the token file" \
"$(printf "$hits")

${BOLD}Fix:${NC} use a token — ${GREEN}bg-primary${NC}, ${GREEN}text-muted-foreground${NC}, ${GREEN}border-border${NC}.
Need a colour that does not exist yet? Add it to ${DIM}${TOKENS}${NC} first, give it a
name, then use the name. If you cannot name it, you probably do not need it."
else
  printf '  %s\n' "${GREEN}✓${NC} no hard-coded hex colours"
fi

# ------------------------------------------------------- 2. rgb()/hsl() literals
hits=""
while IFS= read -r f; do
  is_allowed "$f" && continue
  found=$(grep -nE '\b(rgba?|hsla?)\([0-9]' "$f" 2>/dev/null || true)
  [ -n "$found" ] && hits+="  ${f}\n$(echo "$found" | sed 's/^/      /')\n"
done < <(sources)
if [ -n "$hits" ]; then
  fail "Literal rgb()/hsl() colour outside the token file" \
"$(printf "$hits")

${BOLD}Fix:${NC} same as above — add a named token, then use it."
else
  printf '  %s\n' "${GREEN}✓${NC} no literal rgb()/hsl() colours"
fi

# ------------------------------------------------ 3. Tailwind arbitrary values
hits=""
while IFS= read -r f; do
  is_allowed "$f" && continue
  found=$(grep -nE '\b(bg|text|border|fill|stroke|ring|shadow|from|via|to)-\[' "$f" 2>/dev/null || true)
  [ -n "$found" ] && hits+="  ${f}\n$(echo "$found" | sed 's/^/      /')\n"
done < <(sources)
if [ -n "$hits" ]; then
  fail "Tailwind arbitrary value (the bg-[...] escape hatch)" \
"$(printf "$hits")

${BOLD}Fix:${NC} arbitrary values bypass the token system entirely, which is exactly
what we are preventing. Use a token, or add one."
else
  printf '  %s\n' "${GREEN}✓${NC} no Tailwind arbitrary values"
fi

# ------------------------------------------- 4. Tailwind default palette classes
hits=""
PALETTE='slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose'
while IFS= read -r f; do
  is_allowed "$f" && continue
  found=$(grep -nE "\b(bg|text|border|ring|fill|stroke)-(${PALETTE})-[0-9]{2,3}\b" "$f" 2>/dev/null || true)
  [ -n "$found" ] && hits+="  ${f}\n$(echo "$found" | sed 's/^/      /')\n"
done < <(sources)
if [ -n "$hits" ]; then
  fail "Tailwind's default palette (bg-blue-500, text-slate-700, …)" \
"$(printf "$hits")

${BOLD}Fix:${NC} our preset REPLACES Tailwind's palette, so these produce no styles at
all — the element silently renders unstyled. Use a semantic token instead:
  ${DIM}bg-blue-500   →${NC} ${GREEN}bg-primary${NC}
  ${DIM}text-gray-500 →${NC} ${GREEN}text-muted-foreground${NC}
  ${DIM}border-gray-200 →${NC} ${GREEN}border-border${NC}
  ${DIM}bg-red-600    →${NC} ${GREEN}bg-destructive${NC}"
else
  printf '  %s\n' "${GREEN}✓${NC} no default-palette classes"
fi

# --------------------------------------------------------- 5. font declarations
hits=""
while IFS= read -r f; do
  is_allowed "$f" && continue
  # CSS `font-family:` AND the JSX camelCase `fontFamily:` form.
  found=$(grep -nE '(font-family[[:space:]]*:|fontFamily[[:space:]]*:)' "$f" 2>/dev/null || true)
  [ -n "$found" ] && hits+="  ${f}\n$(echo "$found" | sed 's/^/      /')\n"
done < <(sources)
if [ -n "$hits" ]; then
  fail "font-family declared outside the preset" \
"$(printf "$hits")

${BOLD}Fix:${NC} the type stack is defined once in ${DIM}${PRESET}${NC}.
Use ${GREEN}font-sans${NC} or ${GREEN}font-mono${NC}."
else
  printf '  %s\n' "${GREEN}✓${NC} no stray font-family declarations"
fi

# ------------------------------------------------------- 6. brand colour intact
if [ -f "$TOKENS" ]; then
  if grep -qE -- "--primary:[[:space:]]*${BRAND_PRIMARY_HSL};" "$TOKENS"; then
    printf '  %s\n' "${GREEN}✓${NC} brand primary is ${BOLD}#325DEC${NC} ${DIM}(hsl(${BRAND_PRIMARY_HSL}))${NC}"
  else
    actual=$(grep -E -- '--primary:' "$TOKENS" | head -1 | sed 's/^[[:space:]]*//')
    fail "Brand primary has changed" \
"  expected: ${GREEN}--primary: ${BRAND_PRIMARY_HSL};${NC}   ${DIM}(#325DEC)${NC}
  found:    ${RED}${actual}${NC}

${BOLD}Fix:${NC} #325DEC is the approved brand colour. If it is genuinely changing,
that is a decision to make deliberately — update BRAND_PRIMARY_HSL in this
script in the same commit, and say why in the PR description."
  fi
fi

# ------------------------------------------------------------------------ done
printf '\n'
if [ "$FAILED" -ne 0 ]; then
  printf '%s\n\n' "${RED}${BOLD}Design check failed.${NC} See docs/DESIGN_SYSTEM.md"
  exit 1
fi
printf '%s\n\n' "${GREEN}${BOLD}Design check passed.${NC}"
