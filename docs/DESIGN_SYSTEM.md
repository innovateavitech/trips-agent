# Design system

One brand colour, one typeface, one set of components. This page explains the rules and, more
usefully, why they exist.

---

## The brand

| | |
|---|---|
| **Primary** | `#325DEC` — `hsl(226 83% 56%)` |
| **Typeface** | Inter (variable), self-hosted |
| **Radius** | `0.5rem` |

`#325DEC` gives **5.37:1** against white, which passes WCAG AA for normal text. White text on a
primary button is safe. Lighten it and that stops being true, so if the brand colour ever
changes, re-check the contrast before shipping it.

Inter is self-hosted via `@fontsource-variable/inter` rather than loaded from Google Fonts. Our
users are travel agents in Nigeria, often on slow or metered connections — one fewer third-party
round trip is worth having, and it removes a tracking dependency.

---

## The one rule

> **`packages/ui/src/styles/tokens.css` is the only file in the repository allowed to contain a
> raw colour value.**

Everywhere else uses a semantic token:

```tsx
<div className="bg-primary text-primary-foreground" />   // ✅
<div className="bg-[#325DEC] text-white" />              // ❌ fails check:design
<div className="bg-blue-500" />                          // ❌ renders unstyled
```

### Why this is enforced rather than encouraged

The second a codebase contains two blues, nobody can tell which is correct. The next developer
copies whichever they saw last, a third picks a slightly different one from a mockup, and within
a month the product looks assembled rather than designed — and no single commit is to blame.

Style guides that rely on memory lose to deadlines. A check that fails the build does not.

---

## Tokens

Defined in [`packages/ui/src/styles/tokens.css`](../packages/ui/src/styles/tokens.css) as
`H S% L%` triples (no `hsl()` wrapper) so Tailwind can apply opacity — `bg-primary/10` only works
in that format.

| Group | Tokens |
|---|---|
| **Brand** | `primary`, `primary-foreground`, `primary-hover`, `primary-active`, `primary-subtle`, `primary-border` |
| **Surfaces** | `background`, `foreground`, `card`, `card-foreground`, `popover`, `popover-foreground` |
| **Neutrals** | `secondary`, `muted`, `accent` (each with a `-foreground`) |
| **Status** | `destructive`, `success`, `warning`, `info` (each with a `-foreground`) |
| **Lines** | `border`, `input`, `ring` |

Dark mode redefines the same names under `.dark`. Because components only ever reference token
names, dark mode needs no component changes at all.

### Adding a colour

1. Add a **named** token to `tokens.css` — both light and dark
2. Check its contrast against whatever sits on it
3. Use the name

If you cannot name it, you probably do not need it. "A slightly different blue for this one card"
is the beginning of drift.

---

## Components

Live in [`packages/ui/src/components/`](../packages/ui/src/components/), built with
[`cva`](https://cva.style) so every visual decision sits in one place.

```tsx
import { Button, Input } from '@trips/ui';

<Button>Top up</Button>
<Button variant="outline" size="sm">Cancel</Button>
<Button variant="destructive" loading>Cancel booking</Button>
<Input label="Amount" hint="Minimum ₦1,000" error={errors.amount} />
```

**Need a button that looks different? Add a variant to `button.tsx`.** Do not style it inline at
the call site — that is how a codebase ends up with nine slightly different primary buttons.

Every component takes `className` and merges it through `cn()`, so callers can adjust *layout*
(margins, width) without overriding *identity* (colour, weight, radius). That distinction is the
whole point: position is the caller's business, appearance is the system's.

### Conventions

- **`Input` requires a `label`.** A placeholder is not a label — it vanishes the moment someone
  types, and screen readers do not announce it reliably.
- **`Button` has a `loading` prop.** Use it on anything that hits the API. Agents double-click
  when a page feels slow, and on this product a double-click can mean a double booking.
- **`destructive` is for genuinely destructive actions** — cancelling a booking, issuing a
  refund. Not for "close" or "clear".
- **Never remove the focus ring.** Agents work at speed and many navigate by keyboard.

---

## What is checked

`pnpm check:design` — also a required CI job, and part of `pnpm verify`.

| Check | Catches |
|---|---|
| Hard-coded hex | `#325DEC` outside `tokens.css` |
| `rgb()` / `hsl()` literals | `color: rgb(50, 93, 236)` |
| Tailwind arbitrary values | `bg-[#325DEC]`, `text-[13px]` |
| Default palette | `bg-blue-500`, `text-slate-700` — these render **unstyled**, since our preset replaces Tailwind's palette |
| Stray `font-family` | Both CSS `font-family:` and JSX `fontFamily:` |
| Brand drift | `--primary` no longer being `#325DEC` |

Each failure names the file, the line, and what to use instead.

**All six were verified by deliberately introducing each violation and confirming the check
fails.** A guardrail nobody has seen fail is not a guardrail.

---

## AI design skills

Three skills are installed so anyone using an AI assistant gets consistent design guidance:

| Skill | What it brings |
|---|---|
| **[impeccable](https://github.com/pbakaus/impeccable)** | 23 commands (`/impeccable audit`, `polish`, `critique`) and 61 deterministic detector rules, wired as Claude Code hooks that run on edit and on stop |
| **[frontend-design](https://github.com/anthropics/skills)** | Typography, colour, layout and restraint — "spend your boldness in one place" |
| **[emil-design-eng](https://github.com/emilkowalski/skills)** | Animation and interaction craft |

Skill **content** is committed (`.agents/skills/`, `.claude/skills/**/*.md`) so the standard is
pinned and reviewable. Impeccable's ~12MB platform-specific binary is not — `scripts/setup.sh`
installs it per developer, and its hooks no-op safely if it is absent.

> **One tension worth naming.** Impeccable's guidance explicitly warns against "Inter for
> everything" as a thoughtless default. Inter is nonetheless the right call here: this is a dense,
> data-heavy B2B console where legibility at small sizes beats personality, and Inter's tabular
> figures matter for a product full of fares and wallet balances. The warning is about reaching
> for Inter without thinking. We thought about it.

---

## Adding a shadcn/ui component

Our components follow [shadcn/ui](https://github.com/shadcn-ui/ui) conventions — copy the source
in, own it, adapt it. When pulling one in:

1. Copy it into `packages/ui/src/components/`
2. **Replace every colour with a token.** shadcn ships with its own variable names; map them onto
   ours (`bg-primary`, `text-muted-foreground`, `border-border`)
3. Swap its `cn` import for `../lib/cn`
4. Export it from `src/index.ts`
5. Run `pnpm check:design` — it will catch anything you missed

---

## Related

| | |
|---|---|
| [`packages/ui/src/styles/tokens.css`](../packages/ui/src/styles/tokens.css) | The tokens |
| [`packages/ui/tailwind.preset.ts`](../packages/ui/tailwind.preset.ts) | The shared Tailwind preset |
| [`scripts/check-design.sh`](../scripts/check-design.sh) | The drift detector |
| [CLAUDE.md](../CLAUDE.md) | Rules for AI assistants |
| [WORKING_WITH_CLAUDE.md](WORKING_WITH_CLAUDE.md) | The issue-to-PR loop |
