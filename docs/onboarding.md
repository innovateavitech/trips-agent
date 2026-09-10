# Your first week

Welcome. This is a big system, and nobody expects you to understand all of it. This page is the
order to learn it in.

Do not try to read the whole codebase. Follow these five days, ship one small thing, and the
rest will make sense far faster than reading ever would.

---

## Day 1 — Understand the product before the code

Read, in this order:

1. **[`README.md`](../README.md) §1–§4** — what we are building, who uses it, the glossary, and
   how the pieces fit together. *(~30 minutes)*
2. **The glossary again.** Seriously. This domain uses ordinary words to mean specific things —
   an "agent" is a travel business, not a support person. Getting these ten terms straight will
   save you days.
3. **[`CONTRIBUTING.md`](../CONTRIBUTING.md)** — how we branch, commit and open PRs. *(~20 minutes)*

Then answer these four questions for yourself. If you cannot, re-read §1 of the README:

- Who is Trips' paying customer — the travel agent, or the traveller?
- Why must the traveller never see the Trips brand?
- What is the difference between a *net rate* and a *sell price*?
- Why is money stored as `150000` rather than `1500.00`?

**Optional but useful:** skim the FRD (`docs/FUNCTIONAL REQUIREMENT DOCUMENT.pdf`). It is the
client's own description of what they want, in their own words. You do not need to memorise it —
just know it exists and roughly what is in it.

---

## Day 2 — Get it running

Work through [`README.md` §7](../README.md#7-local-setup) start to finish.

Expect something to break. That is normal — the troubleshooting table covers the five most
likely failures. **Give it 30 minutes, then ask.** Being stuck on setup is the least valuable
possible use of your time.

When it is running, click around as each persona:

- Log in as `agent@demo.test` — this is the Agent Console, where our customer works
- Open the demo storefront — this is what a *traveller* sees. Notice there is no mention of Trips
- Log in as `admin@trips.test` — this is our own back-office

Then open the **Hangfire dashboard** (<http://localhost:5000/hangfire>) and watch the background
jobs tick over. This is the part of the system that is invisible in the UI and does most of the
important work.

---

## Day 3 — Follow one request all the way through

Pick the simplest real thing the system does and trace it end to end. A good one: **an agent tops
up their wallet.**

Follow the path with your editor's "go to definition":

```
apps/agent-console/src/features/wallet/     the button the agent clicks
        ↓
services/TripsAgent.Api/Endpoints/           the endpoint it calls
        ↓
services/TripsAgent.Application/             the use case — what happens
        ↓
services/TripsAgent.Domain/Payments/         the business rules
        ↓
services/TripsAgent.Infrastructure/          how it is saved
        ↓
PostgreSQL                                   the wallet + ledger rows
```

Set a breakpoint at each layer and step through it once. One hour doing this teaches more than a
day of reading.

**The thing to notice:** the API endpoint contains almost no logic. It receives a request, hands
it to the Application layer, and returns a response. That is deliberate — business rules live in
`Domain` so they can be tested without a database or a web server.

---

## Day 4 — Ship something

Pick an issue labelled **`good-first-issue`**. These are chosen to be genuinely small and safe —
they never touch payments, tenancy or the supplier integration.

Then run the loop. If you are using Claude Code, follow
[`WORKING_WITH_CLAUDE.md`](WORKING_WITH_CLAUDE.md) — it covers briefing it and, more importantly,
reviewing what it gives you. Otherwise, [`CONTRIBUTING.md` §4](../CONTRIBUTING.md#4-the-everyday-workflow):

```bash
git checkout main && git pull origin main
git checkout -b fix/123-short-description
# ... work ...
pnpm verify
git add -p
git commit -m "fix(scope): what this does"
git push -u origin fix/123-short-description
gh pr create --fill
```

Your PR will get comments. **That is the process working, not a judgement of you.** Everyone's
PRs get comments, including the lead's. Ask about anything you do not understand — "why is this
better?" is a great review comment to leave.

---

## Day 5 — Learn the shape of the whole thing

Now that you have shipped something, the architecture will land much better.

Read **[`docs/ARCHITECTURE_AND_DELIVERY_PLAN.md`](ARCHITECTURE_AND_DELIVERY_PLAN.md)** — not all
of it, these sections:

- **§1 Architecture** — multi-tenancy and the stack
- **§2 Data model** — skim all of it, then read closely whichever context you will work in
- **§3 Background jobs** — what runs on its own, and why
- **§4 The money path** — how a booking actually completes. This is the hardest and most
  important flow in the system
- **§6 Milestones** — what we are building and in what order

Also read **[`docs/TRIPS_AFRICA_API_NOTES.md`](TRIPS_AFRICA_API_NOTES.md)** if you will touch
flights or buses. It is short, and it documents the traps.

---

## The five things that will bite you

Learn these now and save yourself a painful review.

### 1. Money is never a decimal

```csharp
decimal price = 1500.00m;   // ❌ build fails
long priceMinor = 150000;   // ✅ ₦1,500.00 in kobo
```

Floating point loses fractions. In a ledger that must balance exactly, that is unrecoverable.

### 2. Never bypass the tenant filter

Queries are automatically scoped to the current agency. `.IgnoreQueryFilters()` reads **every
agency's data at once**. If you think you need it, ask first — a mistake here means one travel
agency sees another's customers and prices.

### 3. The traveller must never see "Trips"

Storefront pages, invoices, vouchers and customer emails all use the *agent's* branding. Pull it
from the agency record; never hard-code ours.

### 4. Prices are frozen at purchase

Net rate, markup and tax are copied onto the order line when the order is placed. Do not
recalculate them later — an agent changing their markup must not rewrite last month's revenue.

### 5. The supplier has no webhooks

Trips Africa will not tell us when a ticket is issued; we poll for it. Anything that depends on
a booking's final state belongs in a background job, not a request handler.

---

## Where to ask

The **team channel**, not direct messages. Someone else has the same question, and the answer
becomes searchable.

**Try for 30 minutes, then ask.** Every time. Have ready:

1. What you were trying to do
2. What you expected
3. What actually happened — the exact error text
4. What you already tried

Half the time, writing those four lines solves it. The other half, you get an answer in minutes
instead of losing a day.

Nobody on this team has built a system like this before. Asking early is the professional move.
