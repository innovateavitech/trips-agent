# CLAUDE.md

Context for Claude Code (and any AI assistant) working in this repository.
Humans: this is a useful one-page summary too, but [README.md](README.md) is the real
introduction and [CONTRIBUTING.md](CONTRIBUTING.md) is the real process.

---

## What this is

The **Trips Agent Platform (NG)** — a B2B2C travel SaaS for the Nigerian market.

The chain, which explains almost every design decision here:

```
Trips (us)  →  Travel Agent (our paying customer)  →  Traveller (the agent's customer)
                                                       ↑ must never see the Trips brand
```

Agents sign up, get KYB-verified, search and book flights and buses through the Trips Africa
API, build their own tour/visa/group-tour catalog, publish a branded website on their own
domain, set their own markup, and sell to their own customers. We are the invisible engine.

**Status:** pre-development. Documentation and repo governance exist; feature code starts with
Milestone 1. If you are asked to build something, check
[docs/ARCHITECTURE_AND_DELIVERY_PLAN.md](docs/ARCHITECTURE_AND_DELIVERY_PLAN.md) first — the
schema, the jobs and the milestone order are already designed.

---

## Who you are working with

**The developers on this project are beginners.** This changes how to help them:

- **Explain the why, not just the what.** A diff they don't understand is a liability.
- **Prefer the boring, obvious solution** over the clever one. Clever code is code they cannot
  maintain after you are gone.
- **Never silently do something risky on their behalf.** Say what you are about to do and why.
- **Point at the docs.** `README.md`, `CONTRIBUTING.md`, `docs/onboarding.md` and `docs/adr/`
  exist so answers are findable without asking.
- **Don't assume git fluency.** Spell out commands; explain what a rebase or a force-push does
  before suggesting one.

---

## Hard rules — do not break these

### 1. Never push to `main`
Every change goes through a branch and a pull request. Git hooks in `.githooks/` enforce this.
**Do not suggest `--no-verify` or `ALLOW_MAIN_PUSH=1` to get around a blocked push** — the
correct move is always to branch. Those overrides exist for the repo owner in an emergency, and
that is not the same as a hook being inconvenient.

### 2. Money is `bigint` minor units, never a decimal
```csharp
decimal price = 1500.00m;   // ❌ never
long priceMinor = 150000;   // ✅ ₦1,500.00 in kobo
```
Columns are named `*_minor`. Floating-point money loses kobo, and in a double-entry ledger that
is unrecoverable.

### 3. Never bypass the tenant filter
Every business table has `agency_id` and EF Core filters it automatically.
`.IgnoreQueryFilters()` reads **every agency's data at once**. There are two or three legitimate
uses in the whole codebase, all in platform-admin reporting, all audited. If it seems necessary,
stop and ask the user — a mistake here leaks one travel agency's customers and prices to another.

### 4. Nothing traveller-facing may reference Trips
Storefront pages, invoices, vouchers, emails: all use the **agent's** name, logo and colours from
their branding record. Never hard-code ours. This is the product.

### 5. Prices are frozen at purchase, never recalculated
Net rate, markup and tax are snapshotted onto the order line when the order is placed, and a
database trigger blocks updates afterwards. An agent changing their markup must not rewrite last
month's revenue.

### 6. Never retry the supplier's ticket-issue call
`POST /api/v2/ticketing/issue` is **not idempotent** and has no idempotency key. A timeout is an
*unknown outcome*, not a failure — resolve it by polling `GetBookingStatus`, never by re-issuing.
Retrying issues a second real ticket that cannot easily be refunded.
Full reasoning: [docs/adr/0003-never-retry-ticket-issuance.md](docs/adr/0003-never-retry-ticket-issuance.md)

### 7. Never hard-code a colour, font or spacing value
`frontend/packages/ui/src/styles/tokens.css` is the **only** file allowed to contain a raw colour.
Everywhere else uses a token: `bg-primary`, `text-muted-foreground`, `border-border`.

```tsx
<div className="bg-primary" />          // ✅
<div className="bg-[#325DEC]" />        // ❌ fails pnpm check:design
<div className="bg-blue-500" />         // ❌ our preset replaces Tailwind's palette — renders unstyled
```

Brand primary is **#325DEC**, typeface is **Inter**. Components live in `frontend/packages/ui` and are
built with `cva` — need a different look? Add a variant there, never style inline at the call
site. Run `pnpm check:design` before you finish.
Full guide: [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md)

### 8. Never commit secrets
No API keys, tokens, passwords, `.env`. The pre-commit hook scans for these. If one is
committed by accident, the fix is **rotate it**, not amend the commit — it stays in history.

---

## Git workflow

```bash
git checkout main && git pull origin main
git checkout -b feat/M1-short-description
# ... work ...
git add -p                     # review your own diff
git commit -m "feat(scope): what this does"
git push -u origin feat/M1-short-description
gh pr create --fill
```

**Commit format:** `<type>(<scope>): <subject>` — lowercase, present tense, no full stop,
under 72 chars. Enforced by `.githooks/commit-msg`.

- Types: `feat` `fix` `docs` `refactor` `test` `chore` `perf` `build` `ci` `style` `revert`
- Scopes: `auth` `wallet` `supplier` `catalog` `storefront` `admin` `crm` `ui` `db` `deps`

Branches: `feat/…` `fix/…` `docs/…` `refactor/…` `test/…` `chore/…`

**Merges are squash-only.** Messy branch history is fine — it becomes one commit on `main`.

---

## Issue conventions

Issues are titled **`Module: What it is`** and carry a matching `module:` label — 22 modules from
`Platform Foundation` to `Security`. When asked to work on an issue, read it with
`gh issue view <n>` and check its **Depends on** line: if a dependency is still open, say so
rather than building against something that does not exist yet.

Full map: [docs/BACKLOG.md](docs/BACKLOG.md), regenerated by `./scripts/generate-backlog.sh`.

---

## Repository layout

**The two stacks live in two self-contained roots.** Each owns its own build configuration,
so neither one's tooling has to know the other exists.

```
backend/                    the .NET solution — run dotnet commands from here
  services/                 TripsAgent.{Api,Worker,Domain,Application,Infrastructure,Contracts,Documents}
                            TripsAgent.Integrations.{TripsAfrica,Paystack}
    TripsAgent.Infrastructure/Persistence/Migrations/   EF Core migrations
  tests/                    Unit · Integration (Testcontainers) · Architecture (NetArchTest)
  TripsAgent.slnx           the solution
  Directory.Build.props     compiler settings for every project
  Directory.Packages.props  central package versions — versions live here, not in .csproj
  .config/dotnet-tools.json pinned dotnet-ef, restored by scripts/setup.sh

frontend/                   the pnpm workspace — run pnpm commands from here
  apps/                     agent-console (Vite) · storefront (Next.js) · admin-console (Vite) · marketing-site
  packages/                 ui · api-client (GENERATED) · contracts (GENERATED) · config · utils
  package.json              workspace root scripts
  pnpm-workspace.yaml · turbo.json · pnpm-lock.yaml

e2e/                        Playwright, drives real browsers against both stacks
docs/                       plan · ADRs · runbooks · onboarding · FRD
scripts/                    setup.sh · check-design.sh · ef.sh — shared by both stacks
.githooks/                  shared git hooks (installed by scripts/setup.sh)
```

Because each root is self-contained, the command you run depends on where you are:

```bash
cd backend  && dotnet build          # or dotnet test, dotnet run --project services/TripsAgent.Api
cd frontend && pnpm build            # or pnpm dev, pnpm typecheck, pnpm test
./scripts/check-design.sh            # repo-root scripts work from anywhere
```

**Layering — arrows never point backwards.** Architecture tests fail the build if they do:
```
Api ──▶ Application ──▶ Domain ◀── Infrastructure
```
`Domain` depends on nothing. Business rules go there so they are testable without a database.

**Never hand-edit** `frontend/packages/api-client/` or `frontend/packages/contracts/` — they are generated from
the .NET DTOs. Change the C# and run `pnpm generate:api`.

---

## Stack

.NET 10 · PostgreSQL 16 (EF Core + Npgsql) · Redis · Hangfire (cron) + MassTransit/RabbitMQ
(sagas) · React 19 + TypeScript · Next.js 15 (storefront only, for SEO) · Tailwind + shadcn/ui ·
pnpm + Turborepo · QuestPDF · Paystack · xUnit + Testcontainers + Playwright

Cloud is **not chosen yet** — everything goes through ports (`IBlobStorage`, `IMessageBus`,
`IEmailSender`). Don't introduce a cloud-specific SDK without raising it first.

---

## Domain vocabulary

These words do not mean what you'd guess:

| Term | Meaning |
|---|---|
| **Agent** | A travel *business* that sells under its own brand. Our customer. Not a support agent |
| **Principal / Sub-agent** | An agency that manages other agencies beneath it, and those agencies |
| **Storefront** | The public branded website we generate for an agent |
| **Net rate** | What Trips charges the agent. Never shown to the traveller |
| **Markup** | What the agent adds on top. **Sell price** = net + markup + tax |
| **Wallet** | An agent's prepaid balance, backed by a double-entry ledger |
| **KYB** | Know Your Business — verifying an agency is real before it can transact |
| **PNR** | The airline's booking reference |
| **Ticket Time Limit** | Hard deadline to issue after confirming a price, or the booking dies |
| **Departure** | A dated instance of a tour. **Group departure** = fixed date, min/max pax |

Full glossary: [README.md §3](README.md#3-glossary--read-this-first)

---

## Things that will surprise you

- **The Trips Africa API has no webhooks.** Booking outcomes are learned by polling. Anything
  depending on a final booking state belongs in a background job, not a request handler.
- **The supplier API covers flights and bus only** — no hotels, visas or tours. Visas and tours
  are agent-authored catalog products we host ourselves.
- **Confirm-price returns an array** for domestic and round-trip. Every element's SHA-512 hash
  must be validated independently.
- **The FRD contradicts itself in places.** 27 open questions are listed in
  [§7 of the plan](docs/ARCHITECTURE_AND_DELIVERY_PLAN.md). If a task touches one, flag it rather
  than guessing.

---

## Before you finish a change

- [ ] Does one thing
- [ ] Tests cover the new behaviour
- [ ] No commented-out code, no `Console.WriteLine` / `console.log`
- [ ] No secrets; new config added to `.env.example`
- [ ] New tenant-scoped tables have `agency_id` + a filter
- [ ] Money in minor units
- [ ] Nothing traveller-facing hard-codes the Trips brand
- [ ] README or an ADR updated if a decision changed

---

## Key documents

| | |
|---|---|
| [README.md](README.md) | The product, personas, glossary, setup, "where do I find X" |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Full git workflow, PR process, recovery from mistakes |
| [docs/WORKING_WITH_CLAUDE.md](docs/WORKING_WITH_CLAUDE.md) | How developers here work with you: issue → plan → review → PR → reviewer |
| [docs/BACKLOG.md](docs/BACKLOG.md) | All 70 issues grouped by module, with dependencies and build order |
| [docs/DESIGN_SYSTEM.md](docs/DESIGN_SYSTEM.md) | Tokens, components, and what `check:design` enforces |
| [docs/ARCHITECTURE_AND_DELIVERY_PLAN.md](docs/ARCHITECTURE_AND_DELIVERY_PLAN.md) | Schema, background jobs, checkout saga, milestones, open questions |
| [docs/TRIPS_AFRICA_API_NOTES.md](docs/TRIPS_AFRICA_API_NOTES.md) | Supplier endpoints, hash validation, reversal rules |
| [docs/adr/](docs/adr/) | Why things are the way they are |
| [docs/onboarding.md](docs/onboarding.md) | A new developer's first week |
