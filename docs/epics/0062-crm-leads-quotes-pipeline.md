# Epic #62 — CRM: leads, quotes and pipeline

**Epic:** [#62](https://github.com/innovateavitech/trips-agent/issues/62) ·
**Module:** CRM · **Milestone:** M2 — Storefront, catalog & customer commerce
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Trip-request widget creating leads; pipeline New → Quoted → Negotiating → Won → Lost; quote
> builder with itinerary days and a shareable public accept link; follow-up tasks with reminders;
> communication timeline; customer 360 auto-created from any inquiry, quote or booking.

Twelve issues below cover all six of those. Six are backend behaviour, two are scheduled jobs,
two are the schema and the projection underneath everything else, and two are the agent-console
screens.

---

## The one sentence that shapes this whole epic

From the plan, [§2.10](../ARCHITECTURE_AND_DELIVERY_PLAN.md):

> The profile is auto-created or updated from any inquiry, quote or booking (FRD §2.8 RS-1) —
> customers are never manually keyed in as a precondition.

An agent never sits down and types a customer in. A customer row exists because *something
happened* — a widget submission, a quote, a paid order. That is why `C2` is near the front of the
build order rather than treated as a detail of the CRUD screens: if the projection is added last,
every feature built before it grows its own quiet "create the customer if missing" branch, and
those branches disagree with each other within a month.

The second shaping rule is [hard rule #4](../../CLAUDE.md): **nothing traveller-facing may
reference Trips.** More of this epic is traveller-facing than it first appears — the trip-request
widget, the confirmation email, the public quote link and the quote PDF are all read by the
agent's customer, not by the agent.

---

## Read this before picking anything up

**Do not start `C4` or `C7` yet.** They depend on
[#60](https://github.com/innovateavitech/trips-agent/issues/60) (public storefront rendering) and
[#61](https://github.com/innovateavitech/trips-agent/issues/61) (cart and guest checkout), and
**both of those are epics that have not been broken down.** Their real dependencies are child
issues that do not exist yet, so the `Depends on:` lines below can only be pinned to numbers once
those two breakdowns land.

The rest of M2 is also behind the **five commercial questions** in
[§7 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) — the ones the backlog says must be
answered before M2 begins. `C7` walks straight into questions 2 and 3 the moment an accepted quote
becomes a payable cart.

`C1`, `C2`, `C5`, `C6`, `C9` and `C10` have no such blocker. They need only M1 foundations, and
they are where the epic should start.

---

## The split

`C1`–`C12` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| C1 | Schema for customers, leads, quotes, tasks and communications | ~2 days | #8, #11, #12 |
| C2 | Customer 360 auto-created from any inquiry, quote or booking | ~2 days | C1, #30, #31, #41 |
| C3 | Customer profile API — the 360 view | ~1 day | C1, C2 |
| C4 | Trip-request widget and public lead capture 🚫 | ~2 days | C1, C2, #45, **#60** |
| C5 | Lead pipeline stages and stage history | ~2 days | C1 |
| C6 | Quote builder with itinerary days | ~2 days | C1, C5, #28 |
| C7 | Public quote link — view, accept, decline 🚫 | ~2 days | C6, **#60**, **#61** |
| C8 | `QuoteExpiryJob` | ~1 day | C6, #31, #45 |
| C9 | Follow-up tasks and the reminder job | ~2 days | C1, #31, #45 |
| C10 | Communication timeline | ~1 day | C1, C3, #45 |
| C11 | Agent console — inbox, pipeline board and customer 360 | ~2 days | #48, C3, C5 |
| C12 | Agent console — quote builder and tasks | ~2 days | #48, C6, C9 |

All twelve carry `module:crm` and the `M2` milestone. `C4` and `C7` additionally carry `blocked`
until #60 and #61 are broken down.

**Order:** `C1` first — nothing else compiles without it. Then `C2` and `C5` in parallel, then
`C3` and `C6`. `C9` and `C10` can be picked up by anyone at any point after `C1`; they are the
most self-contained work in the epic and the best entry point for someone new. `C8` follows `C6`.
`C4` and `C7` wait on the storefront epics. `C11` and `C12` last, because a screen built against
an endpoint that is still moving gets built twice.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### C1 · `CRM: Schema for customers, leads, quotes, tasks and communications`

#### What
The `crm` PostgreSQL schema and its EF Core migration — eight tables, per
[the plan §2.10](../ARCHITECTURE_AND_DELIVERY_PLAN.md).

#### Why eight and not seven
The epic body lists seven tables. The plan lists **`customer_documents`** as well, and FRD §2.8
RS-2 requires the profile to show "contact info, booking history, documents and past
communications in one profile". The table is real; the epic body just missed it. It is included
here — see [What this breakdown found](#what-this-breakdown-found).

#### Acceptance criteria
- [ ] `customers`, `customer_documents`, `leads`, `lead_stage_history`, `quotes`, `quote_items`,
      `tasks`, `communications`
- [ ] Every one carries `agency_id uuid NOT NULL`, an index **leading with** `agency_id`, an EF
      Core global query filter, and an RLS policy — the tenancy checklist in
      [CLAUDE.md](../../CLAUDE.md), not a subset of it
- [ ] `customers` has `UNIQUE (agency_id, email) WHERE email IS NOT NULL` — a **partial** unique
      index, because a walk-in customer with no email must still be storable, and two of them
      must not collide on `NULL`
- [ ] `lifetime_value_minor bigint` and `total_bookings int` on `customers`, both owned by `C2`
      and never written by hand from a feature
- [ ] `leads`: `source (trip_request_widget|contact_form|manual)`, destination, date range,
      `budget_min_minor` / `budget_max_minor`, `stage`, `owner_user_id`, `converted_order_id`
- [ ] `quotes`: `quote_number`, `status (draft|sent|viewed|accepted|declined|expired)`,
      `valid_until`, `public_token`
- [ ] `tasks`: polymorphic `related_type` / `related_id`, `due_at`, `reminder_sent_at`
- [ ] `communications`: `channel (email|sms|whatsapp|call|note)`, `direction (inbound|outbound)`
- [ ] **Every money column is `bigint` and named `*_minor`** — budgets and quote item prices
      included. The analyser from [#9](https://github.com/innovateavitech/trips-agent/issues/9)
      should reject anything else, and if it does not, that is a bug in #9 worth reporting
- [ ] `public_token` is at least 128 bits of cryptographically random data, `UNIQUE`, and indexed
- [ ] PII columns are **nullable**, so erasure can null them in place without deleting the row
      (see findings — [#71](https://github.com/innovateavitech/trips-agent/issues/71) depends on
      this)
- [ ] Architecture tests still pass; `Domain` gains entities and stage rules, and depends on
      nothing

#### Notes
`public_token` is the only thing standing between a stranger and a customer's itinerary and
prices. Generate it with `RandomNumberGenerator`, never `Guid.NewGuid()` or `Random` — a v4 GUID
is close enough in practice, but the habit of reaching for it is how a v1 GUID (which encodes a
timestamp and a MAC address) ends up somewhere it matters.

**Depends on:** #8, #11, #12

---

### C2 · `CRM: Customer 360 auto-created from any inquiry, quote or booking`

#### What
The projection that keeps `customers` correct. FRD §2.8 RS-1: the profile is created or updated
automatically from an inquiry, a quote, or a booking.

#### Why this is a message consumer and not a service call
Three different features create customers, and two of them (`C4`, checkout) already have a
transaction of their own that must not be widened. Writing to `outbox_messages` in that same
transaction and projecting from the consumer means a lead is never created without its customer,
and a slow customer upsert never fails a checkout.

#### Acceptance criteria
- [ ] One `UpsertCustomer` application service, keyed on `(agency_id, email)`, falling back to
      phone when there is no email
- [ ] Consumes `LeadCaptured`, `QuoteSent` and `OrderPaid` through MassTransit, deduplicated on
      `inbox_messages.message_id` ([#30](https://github.com/innovateavitech/trips-agent/issues/30))
- [ ] **Merges, never overwrites.** A later booking carrying a fuller name fills a blank field; it
      does not blank a field that already had a value
- [ ] `lifetime_value_minor` and `total_bookings` are **recomputed** from paid orders, not
      incremented — a replayed message must not double-count. This is the same reasoning as
      `wallets.balance_minor` in [§2.9](../ARCHITECTURE_AND_DELIVERY_PLAN.md): the projection is
      a cache, the source rows are the truth
- [ ] Guest checkout with no account still produces a customer row — that is the entire point
- [ ] Idempotent: consuming the same event twice yields exactly one row and identical totals
- [ ] Integration test — one traveller who submits the widget, is quoted, then books, ends as
      **one** `customers` row with `total_bookings = 1`

#### Notes
The natural key is fragile. The same person can inquire as `ade@gmail.com` and book as
`adeola@work.com`, and they are two rows. **Do not attempt fuzzy matching in M2.** A wrong
automatic merge shows one traveller another traveller's booking history and prices; a duplicate
row is an annoyance. Leave a manual "merge these two" action for later and say so in the code.

**Depends on:** C1, #30, #31, #41

---

### C3 · `CRM: Customer profile API — the 360 view`

#### What
The read side of FRD §2.8 RS-2 — everything about one customer in a single response.

#### Acceptance criteria
- [ ] `GET` one customer: contact details, lifetime value, booking history, documents, quotes,
      leads, communications timeline, open tasks
- [ ] Sub-collections are paginated. The timeline is the one that grows without bound, and a
      customer with four years of email history must not return four years of it
- [ ] List and search across name, email and phone, tenant-filtered
- [ ] **No `.IgnoreQueryFilters()`.** [Hard rule #3](../../CLAUDE.md) — there is no legitimate use
      of it in this module
- [ ] The profile is a bounded number of queries, not one per row. Assert it in a test if the ORM
      makes it easy to regress
- [ ] Money returned as minor units with the currency, never pre-formatted into a string

**Depends on:** C1, C2

---

### C4 · `CRM: Trip-request widget and public lead capture` 🚫

#### What
FRD §2.10 RS-2 and RS-3 — the traveller submits destination, dates, budget and preferences on the
agent's public site, and a lead appears in the agent's CRM inbox.

#### Why it is blocked
It needs host-based tenant resolution from
[#60](https://github.com/innovateavitech/trips-agent/issues/60), which is an epic that has not
been broken down yet.

#### Acceptance criteria
- [ ] Anonymous `POST` on the storefront API. The agency is resolved **from the request `Host`**,
      never from a field in the body — a body field lets anyone file a lead into any agency, and
      worse, lets them enumerate which agencies exist
- [ ] Creates a `leads` row with `source = trip_request_widget` and publishes `LeadCaptured`
- [ ] Confirmation email to the traveller in **the agent's** branding — name, logo, colours from
      the branding record. [Hard rule #4](../../CLAUDE.md): the traveller must never see the word
      Trips
- [ ] In-app and email notification to the lead owner
- [ ] Server-side validation: dates in the future and in order, budget within sane bounds, budget
      persisted as `*_minor`
- [ ] Abuse controls — per-IP and per-host rate limiting plus a honeypot field. This is an
      unauthenticated write endpoint on a public website (see findings)
- [ ] The widget itself as a storefront component, using `frontend/packages/ui` tokens only
- [ ] Test: a request with `Host` set to agency A's domain cannot create a lead against agency B

**Depends on:** C1, C2, #45, **#60**

---

### C5 · `CRM: Lead pipeline stages and stage history`

#### What
FRD §2.8 RS-4 — New → Quoted → Negotiating → Won → Lost, and the record of how a lead got there.

#### Acceptance criteria
- [ ] Legal transitions enforced in `Domain`, not in a controller — so the rules are testable
      without a database, which is the whole reason `Domain` depends on nothing
- [ ] Every transition writes a `lead_stage_history` row: from, to, actor, timestamp, reason
- [ ] **Lost requires a reason.** A pipeline you cannot ask "why do we keep losing these?" of is
      a list, not a pipeline
- [ ] Won sets `converted_order_id`, and a lead cannot reach Won without one
- [ ] Re-opening a Lost lead is allowed and recorded. History is append-only; no transition ever
      deletes or edits an earlier row
- [ ] Owner assignment and reassignment, both written to the history
- [ ] Pipeline endpoint grouped by stage with counts and total pipeline value in minor units

**Depends on:** C1

---

### C6 · `CRM: Quote builder with itinerary days`

#### What
FRD §2.10 RS-4 — the agent turns a lead into a priced itinerary.

#### Acceptance criteria
- [ ] Create and edit a draft quote against a lead or a customer, with `quote_items` and per-day
      itinerary entries
- [ ] Items are polymorphic the way cart items are ([§2.8](../ARCHITECTURE_AND_DELIVERY_PLAN.md)):
      a catalog product, a group departure, or a free-text line the agent types themselves
- [ ] Pricing comes from the engine in
      [#28](https://github.com/innovateavitech/trips-agent/issues/28), and the quote stores the
      **same breakdown an order line stores** — net, markup, tax, gross, all `*_minor`
- [ ] `valid_until`, defaulting to an agency-configurable window
- [ ] `quote_number` unique per agency and sequential — **not** gapless, and the issue says why
      (see findings)
- [ ] Sending sets `status = sent`, **freezes the item prices**, and publishes `QuoteSent`
- [ ] Editing a sent quote creates a **new version** rather than mutating it. The customer may be
      looking at the old one right now, and the link they were sent must keep showing what they
      were actually offered
- [ ] Test: a sent quote's totals do not move when the agency edits its markup rule
      ([hard rule #5](../../CLAUDE.md))

**Depends on:** C1, C5, #28

---

### C7 · `CRM: Public quote link — view, accept, decline` 🚫

#### What
The shareable link the traveller opens to see and accept their quote.

#### Why it is blocked
Accepting a quote produces a cart, and cart and guest checkout are
[#61](https://github.com/innovateavitech/trips-agent/issues/61) — an epic that has not been broken
down. It also lands on open question 21 (see findings).

#### Acceptance criteria
- [ ] Unauthenticated page on the storefront at a `public_token` URL. **No account required**
- [ ] Rendered entirely in the agent's branding. [Hard rule #4](../../CLAUDE.md)
- [ ] First open flips `sent` → `viewed` and notifies the agent. Later opens do not re-notify —
      an agent who gets six emails because the customer refreshed will turn notifications off
- [ ] Accept → `accepted`, the lead moves to Won, and a cart is built from the quote items so
      payment goes through the normal checkout rather than a second payment path
- [ ] Decline → `declined` with an optional reason, and the lead moves to Lost
- [ ] An expired, declined or already-accepted quote renders a clear explanation — not a 404, and
      never a payable form
- [ ] `valid_until` is checked **on every request**, not only by the nightly job (see `C8`)
- [ ] The token never appears in an email subject line, a page title, a redirect to an external
      site, or a log line. A `Referer` header leaks a URL to whatever the page links out to

**Depends on:** C6, **#60**, **#61**

---

### C8 · `CRM: QuoteExpiryJob`

#### What
Job 18 in [§3 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) — expires quotes past
`valid_until` and notifies the agent.

#### Acceptance criteria
- [ ] Daily Hangfire job moving `sent` and `viewed` quotes past `valid_until` to `expired`
- [ ] Notifies **the agent, not the traveller.** Nobody needs an email telling them an offer they
      ignored has lapsed
- [ ] `accepted` and `declined` quotes are never touched
- [ ] Idempotent — a second run the same day expires nothing extra and sends nothing extra
- [ ] Logs the count it expired, so a run that suddenly expires 4,000 quotes is visible

#### Notes
The job is **tidying, not enforcement.** A quote must stop being payable the instant it passes
`valid_until`, which is `C7`'s per-request check — not at 02:00 tomorrow when this job runs. If
the only expiry check lives here, there is a window of up to 24 hours in which a traveller can
pay against a stale price. Build `C7`'s check first and treat this job as the thing that makes
the list view honest.

**Depends on:** C6, #31, #45

---

### C9 · `CRM: Follow-up tasks and the reminder job`

#### What
FRD §2.8 RS-3 and RS-4, and job 19 in the plan — the agent sets a reminder, and the system chases
them for it.

#### Acceptance criteria
- [ ] Tasks against a customer, a lead or a quote through `related_type` / `related_id`
- [ ] `due_at`, assignee, status, completion, and completion is recorded with who and when
- [ ] `AgentTaskReminderJob` every 15 minutes: due tasks produce an in-app notification and an
      email
- [ ] `reminder_sent_at` is written **in the same transaction** as the decision to send. Two
      overlapping job runs must not both decide the same task is due
- [ ] Due dates respect the agency's timezone — "due today" for an agent in Lagos is not UTC
      midnight, and a reminder that fires at 01:00 WAT is worse than no reminder
- [ ] Overdue tasks are queryable for the console dashboard

**Depends on:** C1, #31, #45

---

### C10 · `CRM: Communication timeline`

#### What
Everything that has been said to this customer, in one chronological list.

#### Acceptance criteria
- [ ] Agents log a call or a note by hand; email and SMS sent through
      [#45](https://github.com/innovateavitech/trips-agent/issues/45) are recorded automatically,
      so the timeline is not something anyone has to remember to maintain
- [ ] `channel` and `direction` as modelled in `C1`. **`whatsapp` is manual-log-only in M2** —
      no provider exists (see findings), and the enum value must not imply otherwise
- [ ] Merged with lead stage changes and quote events into one ordered view on the profile
- [ ] Paginated, newest first
- [ ] Attachments reuse the asset pipeline in
      [#18](https://github.com/innovateavitech/trips-agent/issues/18) — not a second upload path
- [ ] The issue notes that this table stores message bodies and is therefore **the densest PII in
      the product**, in scope for retention and anonymisation under
      [#71](https://github.com/innovateavitech/trips-agent/issues/71)

**Depends on:** C1, C3, #45

---

### C11 · `CRM: Agent console — inbox, pipeline board and customer 360`

#### What
The screens an agent lives in: new leads, the board, and one customer.

#### Acceptance criteria
- [ ] Inbox of new leads with source and age
- [ ] Pipeline board by stage; drag-to-move calls `C5`'s transition endpoint and shows the Lost
      reason prompt rather than moving optimistically and failing silently
- [ ] Customer 360 screen — profile, bookings, quotes, documents, timeline, tasks
- [ ] Search across name, email and phone
- [ ] Built from `frontend/packages/ui` with `cva` variants. **No hard-coded colour, font or spacing**
      ([hard rule #7](../../CLAUDE.md)); `pnpm check:design` passes
- [ ] Empty states are written, not left blank. An agency's first week has no leads, and this is
      the screen they will judge the product on

**Depends on:** #48, C3, C5

---

### C12 · `CRM: Agent console — quote builder and tasks`

#### What
The quote-building screen and the agent's task list.

#### Acceptance criteria
- [ ] Add items, build the per-day itinerary, see live totals from the pricing engine, preview
      the traveller's view, send
- [ ] The public link is copyable, and the quote's `sent` / `viewed` / `accepted` status is
      visible at a glance
- [ ] Preview renders in the **agent's** branding, so what the agent checks is what the traveller
      gets
- [ ] Task list grouped into overdue, due today, and upcoming
- [ ] Design system rules as `C11`; `pnpm check:design` passes

**Depends on:** #48, C6, C9

---

## What this breakdown found

### Gaps with no owner

1. **`customer_documents` is missing from the epic body.** The plan
   [§2.10](../ARCHITECTURE_AND_DELIVERY_PLAN.md) lists it and FRD §2.8 RS-2 requires documents on
   the profile. Folded into `C1`; the epic body should be corrected so the next reader is not
   confused by the mismatch.

2. **Nothing owns the quote PDF.** FRD §2.10 RS-5 has the traveller receiving the quote by email,
   and the M2 scope line in the plan names "quote PDFs" explicitly — but
   [#46](https://github.com/innovateavitech/trips-agent/issues/46) covers **invoices and vouchers
   only**, and no other issue mentions them. **Recommendation: extend #46 with a third
   `doc_type`** rather than adding a thirteenth issue here. The template record, the render worker
   and the storage path are identical, and duplicating them is exactly how a codebase ends up with
   two PDF renderers that drift.

3. **The public widget ships in M2 with no rate limiter.** `C4` is an unauthenticated write
   endpoint on a public website. The platform rate limiter is `S1` of
   [#71](https://github.com/innovateavitech/trips-agent/issues/71), which is **M3** — so on the
   current plan the widget is live for a milestone without one. `C4` carries a minimal per-IP
   limit and a honeypot as an interim measure, explicitly superseded when `S1` lands. Worth
   raising with whoever owns the #71 breakdown.

4. **No WhatsApp provider exists anywhere in the plan.** `communications.channel` includes
   `whatsapp`, and [#45](https://github.com/innovateavitech/trips-agent/issues/45) covers email
   and SMS. `C10` therefore treats WhatsApp as a **manually logged** channel only. A real WhatsApp
   Business integration is in no milestone — given the FRD's own framing that Nigerian agents sell
   over WhatsApp today, that is probably a product conversation rather than an oversight, but it
   should be a deliberate decision.

5. **`quote_number` gaplessness is undefined.** `document_number_sequences`
   ([§2.11](../ARCHITECTURE_AND_DELIVERY_PLAN.md)) is gapless under a row lock because tax law
   requires it of invoices. **A quote is not a tax document.** Recommendation: per-agency
   sequential, **not** gapless, and not routed through
   [#47](https://github.com/innovateavitech/trips-agent/issues/47) — otherwise every draft quote
   an agent abandons takes a row lock on a shared sequence, and drafts are abandoned constantly.

### Blocked

- **`C4` and `C7`** depend on [#60](https://github.com/innovateavitech/trips-agent/issues/60) and
  [#61](https://github.com/innovateavitech/trips-agent/issues/61), which are themselves
  un-broken-down epics. Their dependency lines cannot be pinned to real issue numbers until those
  breakdowns exist. They should be created carrying `blocked`.
- **All of M2** is behind the five commercial questions the
  [backlog](../BACKLOG.md#blocked-on-client-answers) lists as blocking. `C7` in particular hits
  questions 2 and 3 the moment an accepted quote becomes a payable cart.

### Open questions this touches

- **21 — do storefront travellers get accounts?** `C7` is built on the `public_token` link, which
  is the plan's own recommendation (guest checkout plus a magic link). If the client answers
  "yes, accounts", `C4` and `C7` both change shape and `C2`'s natural key stops being a guess.
- **26 — NDPA erasure as anonymisation.** The `crm` schema is where most of the platform's PII
  lives: `customers`, `customer_documents`, and `communications` message bodies. `S5` of
  [#71](https://github.com/innovateavitech/trips-agent/issues/71) is blocked and already lists
  #62 as a dependency. `C1` keeps PII columns nullable and out of denormalised copies so that
  anonymising in place stays possible — decided now, because retrofitting it means a data
  migration over the table that matters most.
- **9 — price-change re-consent.** A quote accepted a week after it was sent still shows the
  price it was sent at ([hard rule #5](../../CLAUDE.md)), which is correct for the agent's own
  catalog products. For any supplier-sourced line it is not the live price, and `C7`'s cart
  hand-off inherits whatever tolerance band question 9 settles on. Not a blocker for `C6`; it is
  a blocker for quoting flights, which nothing in this epic does yet.
