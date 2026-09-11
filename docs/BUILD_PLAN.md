# Build plan

Everything still to build, as whole features in the order to build them. It was drawn from every GitHub issue (86 of them, 40 open) on 11 September 2026, and **from now on this file is the plan**: work is chosen from it, and a PR ticks its boxes here.

Where each part of the system is designed — tables, jobs, the money path — is in [ARCHITECTURE_AND_DELIVERY_PLAN.md](ARCHITECTURE_AND_DELIVERY_PLAN.md). This file says what to build and when; that one says how.

## How we work now

- **An MVP, not perfection.** The goal is a working MVP. Meet each criterion the simplest way that is still safe with money and with tenants; what the MVP leaves out is listed below. When a question comes up, decide what is best for now, write the decision under *Decisions for the MVP*, and carry on.
- **Four PRs close this out**, in the order below. Each one finishes every feature in it — schema, API, jobs, screens and tests, with no stand-ins left — and is merged before the next is opened; work on the next starts the moment the last is pushed. Work for a later PR may begin early on its own branch, but the PRs merge in order.
- **Tick the boxes in the same PR.** The PR that builds something ticks its criteria here, so `main` always says what is built. Write `Closes #n` for every issue the PR finishes, so the issues close too.
- **Stand-ins are a step, not the end.** Screens may be built against a stand-in behind a port while their backend is in flight. The feature is not done until the stand-in is gone.
- **The hard rules do not change.** Every rule in [CLAUDE.md](../CLAUDE.md) holds for every feature: money in minor units, the tenant filter, nothing traveller-facing mentions Trips, prices frozen at purchase, ticket issuance never retried, design tokens only, no secrets.
- **Open questions still stop work.** Where a feature meets one of the client questions in [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client), flag it in the PR rather than guessing. Each feature below lists the ones it meets.
- **Issues are history.** They stay for discussion and links; nobody needs to open new ones to start work. A decision worth keeping goes into this file.

## The four PRs

| PR | Features | Boxes ticked |
|---|---|---|
| **PR 1** · Milestone 1: the money path | F1 Booking pipeline and checkout (in progress); F2 Notifications and documents (in progress) | 9 of 72 |
| **PR 2** · Milestone 2: the agent's own shop | F3 Product catalog (in progress); F4 Storefront (queued); F5 Customer commerce (queued); F6 Group tours (queued); F7 CRM (queued) | 0 of 74 |
| **PR 3** · Milestone 3: running and charging for the platform | F8 Admin console (queued); F9 Subscriptions and billing (queued); F10 Sub-agent network (queued); F11 Analytics and reporting (queued); F12 Payouts, disputes and reconciliation (queued); F13 Loyalty and reviews (queued (flag only)) | 0 of 42 |
| **PR 4** · Launch readiness | F14 Security and launch readiness (queued) | 0 of 50 |


## Where things stand

The first place to look when picking this up again. Update it whenever a branch lands.

**On `main`:** Milestone 1's foundation (see *Already built*), supplier search (#158), the booking and bookings screens against stand-ins (#159), rate limiting and the retention purge (#165), and this plan.

| Branch | PR | What it holds | State (11 September, evening) |
|---|---|---|---|
| `feat/M1-ticket-issuance` | 1 | F1: ticket issuance, the status poller and the time-limit monitor (#36–#38, committed); checkout saga, reversal and the resolution backend (#42–#44) in progress; then the booking screens switch to the real API | agent working, not pushed |
| `feat/M1-notifications-documents` | 1 | F2: notifications (#45) and invoice and voucher PDFs (#46); documents in the booking screens | agent working, not pushed |
| `feat/M2-catalog-api` | 2 | F3 backend: catalog schema, publish rules and product API (#160 and #161, all criteria met); ClamAV scanning (#18); the Package pricing type | pushed, done |
| `feat/M2-catalog-screens` | 2 | Console: catalog list and editor and the pricing product picker (#162–#164); group departure screens (F6); CRM screens (F7), all against stand-ins; the catalog backend merged in, with the real catalog adapter next | pushed |
| `feat/M2-storefront` | 2 | F4: site builder, domains and the public storefront (#58–#60) | agent working, not pushed |
| `feat/M2-crm` | 2 | F7 backend (#62), based on the console branch | paused, not started |
| — | 2 | F5 customer commerce (#61) and the F6 group tours backend (#57) | not started: F5 needs F1 and F4, F6 needs F3 |
| — | 3, 4 | F8–F14 | not started |

**Next:** when both PR 1 branches are pushed, merge them into one branch, regenerate the API client, run every gate, tick F1 and F2 here, open PR 1 with `Closes` for every finished issue, and merge it once CI is green. Then PR 2 the same way.

## Decisions for the MVP

The client questions in [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client) are answered here for the MVP, so no work waits on them. Each is the plan's own recommendation where it made one; revisit any of them if the client answers differently.

1. **Supplier credentials:** the platform holds one Trips Africa merchant account. Per-agency credentials stay possible in the schema, unused.
2. **Merchant of record for payments:** the platform, through Paystack; money settles into the agency's wallet. Split settlement to agency subaccounts comes after the MVP.
3. **Who fronts the money:** agencies pre-fund their wallets. A booking holds the wallet before the supplier confirms and captures it when the ticket issues; a card payment in the console tops the wallet up first. Credit for agencies comes after the MVP.
4. **Platform fee:** taken from the agency's margin, never added to the traveller's price.
5. **Flight cancellation:** an offline request in the MVP — the console records it and support handles it with Trips Africa. Bus cancellation uses the API.
6. **Sub-agent branding:** one site per tenant; sub-agents sell under the principal's brand.
7. **Hierarchy depth:** two levels, principal and sub-agent.
8. **Wallet allowance:** a hard cap on what a sub-agent may draw from the principal's wallet.
9. **Price-change re-consent:** any rise needs consent; a fall passes through.
10. **Time limit and slow payment rails:** a live countdown at checkout; payment methods that are not instant are hidden when under 30 minutes remain.
11. **Publish gate:** a site can go live with at least one published product or flight search switched on.
12. **Cancelled group departures:** everyone who paid gets a full refund to where the money came from. Cancelling a departure puts each paid booking on it in the resolution queue as a refund.
13. **Unpaid installments:** never cancelled automatically; flagged to the agency at T+7 with a suggested action.
14. **Suspended agencies:** existing bookings stand, travellers keep their documents through their magic link, and support services them; no new bookings, and the storefront goes offline.
15. **Downgrades:** existing usage is kept, new usage is blocked, and the agency gets 30 days' notice to put it right.
16. **Search speed:** the 5-second target is measured on cached results; a live supplier search is bounded by its 20-second timeout.
17. **Currency:** each agency sells in its own base currency only (NGN for now).
18. **Card data:** never touches our servers; cards are entered only on Paystack's hosted page (PCI SAQ-A).
19. **Email sender:** a neutral sending domain with no Trips branding, the agency's name as the display name and its address as reply-to. Per-agency sending domains come after the MVP.
20. **Subdomain squatting:** a reserved-word and known-brand denylist; flagged claims go to manual review.
21. **Traveller accounts:** none — guest checkout with a magic-link "manage my booking" page.
22. **Multi-city and return confirmations:** one order line per priced journey, with a child row for each supplier confirmation.
23. **Platform fee on installments:** taken in proportion with each payment.
24. **Loyalty and reviews:** only the entitlement flag in the MVP.
25. **Seller on invoices:** the agency, in its own name; the platform bills the agency separately for its fees.
26. **NDPA:** erasure is PII anonymisation that keeps financial and audit records; the retention purge stays a dry run until counsel reviews the table; hosting goes to the nearest region once a cloud is chosen.
27. **Supplier unknowns:** the poller keeps its conservative defaults; the questions go to Trips Africa in writing, outside engineering.

Decided during the build:

- **File storage:** the MVP stores uploads with the local storage adapter behind `IBlobStorage` (signed URLs), scanned by ClamAV when one is configured. An S3-compatible adapter (`AWSSDK.S3`, which also covers MinIO) is added once a cloud is chosen.
- **Catalog prices:** the price an agent types is the base price, and markup rules, where the agency sets any, apply on top. With no rule, the customer pays what was typed.
- **Product descriptions** are plain text wherever they are shown.
- **Stand-ins:** each screen defaults to its real endpoint; a stand-in stays only behind demo mode (`VITE_AUTH_MODE=mock`), so the console can still be shown without a backend.
- **Visa document capture in the booking flow** (#53) is left out: visas are sold as catalog products with their own checklist.

## What the MVP leaves out

Each feature meets its criteria the simplest safe way. These wait until after the MVP:

- **F2:** full templates for flights and buses; one plain template serves tours, visas and group departures. No SMS or WhatsApp sending.
- **F4:** two site templates and a fixed set of blocks (hero, product grid, text, contact). SSL issuance and renewal go through a port with a development adapter; a real ACME adapter follows once hosting is chosen.
- **F5 and F6:** installment reminders, but no automatic charging of saved cards; waitlist offers by email only.
- **F7:** SMS and WhatsApp are logged, not sent.
- **F8:** the dashboard shows core counts and sales; the top-agent leaderboard and feature flags wait.
- **F9:** monthly billing only; promotions wait.
- **F11:** CSV exports only, no XLSX; scheduled reports wait.
- **F12:** bank accounts verified by hand; reconciliation from Paystack's settlement export.
- **F14:** a written penetration-test scope and an internal checklist run, with the external test after launch; one recorded load-test run.

## PR 1 · Milestone 1: the money path

**Why here:** Everything later sells through it, it carries the most risk — real tickets, real money — and Milestone 1's acceptance tests already say what done means.

**Done when:** Every box in F1 and F2: a ticket issued end to end against Trips Africa staging, a forced failure provably reversed, a tampered hash blocking issuance, concurrent submits producing one ticket, the ledger soak holding, every booking producing its invoice and voucher, and the booking screens running on the real API.

### F1 · Booking pipeline and checkout

**M1 · In progress** · branch `feat/M1-ticket-issuance` · 9 of 58 boxes ticked

A booking placed in the console ends in a real ticket issued exactly once, or in money provably returned. The ticket-issue call is never retried; the poller learns every outcome; the time-limit monitor fails bookings that ran out of time; the checkout saga holds the wallet before the supplier confirms, captures it on Ticketed and releases it on failure; failed lines land in the agent's resolution queue. The booking and bookings screens already exist against stand-ins, and switch to these endpoints in the same PR.

- **Needs:** Built: price confirmation (#35), orders (#41), wallet and ledger (#22–#27), outbox and messaging (#30, #31).
- **Issues:** #36, #37, #38, #42, #43, #44, #53, #54, #34
- **Open questions it meets:** 1, 3, 5, 9, 10 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)
- **Done when:** Milestone 1's acceptance (plan §6): a ticket issued end to end against Trips Africa staging; a forced failure proves the reversal; a tampered hash blocks issuance; concurrent submits produce exactly one ticket; `wallet.balance = SUM(ledger entries)` holds after a randomised soak.

#### #36 · Ticket issuance with a zero-retry policy

`POST /api/v2/ticketing/issue` — the single most dangerous call in the system.

- [ ] **Retries explicitly disabled** on this call, with a code comment linking the ADR
- [ ] Timeout 45s; on timeout → state `IssueOutcomeUnknown`, hand off to the status poller, **never re-issue**
- [ ] Four-layer double-issue guard: API idempotency key, Redis lock, saga state, and `UNIQUE (order_line_id)` — the constraint is the one that actually holds
- [ ] `TripType` / `TripMode` mapped correctly (`International|Domestic` × `Flight|Road`)
- [ ] **Chaos test:** 20 concurrent issue messages for one order line → exactly one supplier call reaches WireMock's request journal
- [ ] **Kill test:** worker killed mid-issue → restart recovers via `GetBookingStatus`, never re-issues

*Needs first:* #35

#### #37 · Supplier booking status poller

A recurring job that polls `GetBookingStatus` for every booking in a non-terminal state.

- [ ] Runs every 30s over the `(status, next_poll_at)` index
- [ ] Backoff: 30s → 1m → 2m → 5m → 15m → 30m → 1h, up to `ticket_time_limit` + buffer
- [ ] Row-locked so many workers can run safely
- [ ] `2` → emit `BookingTicketed`
- [ ] `0`, `1`, `11` → emit `PaymentReversalRequired`
- [ ] `100` → raise an admin alert
- [ ] **`TicketPending` never resolves itself by timeout** — money stays held and polling continues
- [ ] Every poll written to `supplier_status_polls` as the evidence trail

*Needs first:* #36

#### #38 · Ticket time limit expiry monitor

A job watching `ticket_time_limit` on held bookings.

- [ ] Runs every minute
- [ ] Warns the agent at T-60m and T-15m
- [ ] On expiry: mark the booking failed, release the wallet hold, flag the order line for resolution, notify
- [ ] **No-op if the confirmation was already consumed** — must not clobber a successful booking
- [ ] Fires exactly once per booking, proven by a triple-run test

*Needs first:* #37

#### #42 · Checkout saga

The MassTransit state machine orchestrating payment → confirm → hash → issue → ticket.

- [ ] Wallet hold placed **before** the supplier confirm; captured only on `Ticketed`; released on failure
- [ ] Insufficient balance fails fast, before any supplier call
- [ ] Hash validation gates issuance — unreachable unless every confirmation validated
- [ ] Price change pauses for explicit re-consent within `ticket_time_limit`
- [ ] Every state change writes events to the outbox in the same transaction
- [ ] Session-keyed queues so one booking's messages stay ordered
- [ ] Timeouts on every waiting state — nothing hangs forever

*Needs first:* #36, #23, #41, #31

#### #43 · Payment reversal worker

Consumes `PaymentReversalRequired` and returns the customer's money, per the supplier's exact documented rules.

- [ ] **Evidence-based:** never reverses without a persisted `supplier_status_polls` row justifying it
- [ ] Refund to gateway or credit to wallet, depending on how it was paid
- [ ] Balanced reversing ledger entries
- [ ] Idempotent — a repeated message produces exactly one refund
- [ ] Retries with backoff; escalates to an admin alert after N failures
- [ ] Contract test per rule: each condition produces exactly one `refunds` row, a balanced ledger transaction, and a flagged order line

*Needs first:* #37, #22

#### #44 · Agent resolution queue for failed order lines

FRD §2.4 RS-5 — when a line fails to confirm with the supplier **after** payment, it is flagged for the agent, not silently refunded.

- [ ] Line moves to `failed_needs_resolution` with `resolution_status = open`
- [ ] The customer is notified that one item needs attention — honestly, without alarm
- [ ] The agent gets a work queue of open lines with full context: what failed, why, what they paid
- [ ] Agent can choose **retry**, **substitute** or **refund**
- [ ] Every action written to `audit_logs` and `order_status_history`
- [ ] Resolution is time-tracked so slow ones can be escalated

*Needs first:* #42

#### #53 · Booking flow screens

Traveller details → review → confirm → ticketed.

> Screens on main since #159, against a stand-in. Visa capture waits on a rule for which routes need which visa; the voucher link waits on F2.

- [x] Traveller form per passenger with type-appropriate fields (ADT/CHD/INF)
- [ ] Passport and visa document capture where the route requires it
- [x] Review screen showing the full price breakdown before commitment
- [x] **Price-change re-consent modal** if the confirmed price differs from the searched price
- [x] **Live `TicketTimeLimit` countdown** during the flow
- [x] Payment method selection (wallet or gateway)
- [x] `TicketPending` handled honestly: "we're confirming with the airline", with live status — not a fake success
- [ ] Double-submit prevented client-side as well as server-side
- [ ] Success shows PNR and a link to the voucher

*Needs first:* #52, #42

#### #54 · Bookings list, detail and resolution queue screens

Where an agent manages what they have sold — including the things that went wrong.

> Screens on main since #159, against a stand-in. The date filter and documents on the detail page are still to build; downloads wait on F2.

- [ ] Bookings list with filters by status, date, product type, and search by PNR or traveller name
- [ ] Detail view: travellers, segments, price breakdown, documents, full status timeline
- [x] **Resolution queue** surfacing `failed_needs_resolution` lines prominently — this is money at risk and must not be buried
- [x] Retry / substitute / refund actions with confirmation
- [ ] Voucher and invoice download, plus reissue
- [x] Status badges that are honest about `TicketPending` rather than implying success

*Needs first:* #48, #44

#### #34 · Trips Africa bus/road search

`POST /api/Bus/SearchBus` — the same pipeline as flights, different mapper.

> Bus search is on main (#158). Open on the supplier's side: there is no documented round-trip bus search (a return is two one-way searches) and no terminal list endpoint, so the bus screens use placeholder terminals.

- [ ] Request shape differs from flights (`Parameter.TravelRoute`, `DepartureId`/`ArrivalId` terminal IDs) — mapped correctly
- [ ] Seat inventory (`AvailableSeats`, `SeatNumbers`, `ReservationId`) captured
- [ ] One-way and round-trip
- [ ] Terminal ID reference data sourced and seeded
- [ ] Contract tests against the documented samples

*Needs first:* #32

### F2 · Notifications and documents

**M1 · In progress** · branch `feat/M1-notifications-documents` · 0 of 14 boxes ticked

Every booking produces the paperwork a traveller expects, in the agent's brand: an invoice and a voucher as PDFs, numbered without gaps, emailed to the customer and downloadable from the console; and the emails that tell agents and customers what happened.

- **Needs:** F1, for the booking events that trigger them. Built: gapless numbering (#47), the asset pipeline to store PDFs.
- **Issues:** #45, #46

#### #45 · Notification dispatcher and email templates

`notification_templates`, `notifications`, and a queue-driven dispatcher.

> Partly built (86def9c): the dispatcher sending through the outbox, a versioned template catalog, agency branding on traveller mail, and a suppression list for undeliverable addresses. Check each box below against the code before building it.

- [ ] `IEmailSender` port; SMTP adapter pointed at Mailpit locally
- [ ] Templates per channel and locale, versioned
- [ ] **Rendered with the agent's branding** — logo, colours, name from `agency_branding`
- [ ] Retry with backoff, dead-letter after 5, bounce handling
- [ ] Delivery status recorded per notification
- [ ] M1 templates: verify email, password reset, KYB approved, KYB rejected, wallet top-up receipt, booking confirmed, booking needs attention
- [ ] `dedupe_key` so a triple job run sends exactly one email

*Needs first:* #31

#### #46 · Branded invoice and voucher PDF generation

FRD §2.9 — QuestPDF documents produced when an order line reaches Confirmed.

> Also finishes the console: the voucher link on the ticket step (#53), and voucher and invoice download and reissue on the booking detail (#54).

- [ ] Templates per product type (flight, bus, tour, visa, group departure)
- [ ] Agent's logo, colours and contact details injected from `agency_branding`
- [ ] Rendered in the customer's currency
- [ ] Stored as an asset; downloadable by agent and customer; emailed to the customer
- [ ] **Reprint is byte-identical** to the original (sha256 match)
- [ ] **Reissue** creates a new document with `issue_number = 2` and `supersedes_document_id` set — the original is never mutated
- [ ] Generated asynchronously via the queue, not in the request

*Needs first:* #45, #41

## PR 2 · Milestone 2: the agent's own shop

**Why here:** It is the product's promise: an agent selling under their own brand, on their own site. Inside it, each feature needs the one before — something to sell, somewhere to sell it, a way to pay — with group tours and the CRM on top.

**Done when:** Milestone 2's acceptance (plan §6): a custom domain serves the site over valid SSL; publishing is gated on a published product (see open question 11); concurrent reservations never oversell a departure; a group deposit creates a correct installment schedule; a lead → quote → paid order is traceable end to end; a post-payment supplier failure reaches the resolution queue and notifies the customer.

### F3 · Product catalog

**M2 · In progress** · branch `feat/M2-catalog-api (backend) and feat/M2-catalog-screens (console), landing as one PR` · 0 of 36 boxes ticked

Agents build tours, packages and visa listings — itinerary, inclusions, prices by room, age and group size, images, categories and themes — and publish them once they pass the publish rules. The pricing screen then picks a product by name instead of an ID.

- **Needs:** Built: the asset pipeline (#120), pricing (#28).
- **Issues:** #56, #160, #161, #162, #163, #164, #18
- **Decided:** `products.available_from` / `available_to` is the "one available date" the publish rule needs. Dated departures with capacity stay with group tours (F6).

#### #56 · Tours, packages and visas

Agent-authored sellable products.

**Tables:** `products, product_media, product_categories, tour_itinerary_days, product_inclusions, product_price_variants, visa_details, visa_document_requirements`

> The epic. Its breakdown is #160–#164 below.

- [ ] Day-by-day itinerary builder.
- [ ] Inclusions/exclusions.
- [ ] Price variants by room type, group size and child/infant.
- [ ] Category and theme tagging.
- [ ] Draft vs published with validation (title, price, one image, one available date).
- [ ] Visas as agent-authored listings with a document checklist and manual fulfilment.

#### #160 · Product schema, domain and publish rules

The tables for agent-authored products (tours, packages and visas), and the rules for when one can be published. Part of #56.

- [ ] Tables as in [the plan §2.5](../blob/main/docs/ARCHITECTURE_AND_DELIVERY_PLAN.md): `products`, `product_media`, `product_categories` + `product_category_map`, `tour_itinerary_days`, `product_inclusions`, `product_price_variants`, `visa_details`, `visa_document_requirements`. Each has `agency_id`, the tenant filter, a row-level security policy and grants (ADR-0006)
- [ ] UNIQUE `(agency_id, slug)` and UNIQUE `(product_id, day_number)`
- [ ] Money in `*_minor`; price variants by pax type, occupancy and group size
- [ ] `available_from` / `available_to` on `products`: the "one available date" a tour or package needs before it can be published. Dated departures stay with #57
- [ ] Publish rules live in the domain: a title, a price, at least one image, and for a tour or package an availability window that has not ended. A visa needs its details and at least one required document
- [ ] Draft → Published → Archived. "Why can't I publish?" returns **every** problem, not just the first

*Needs first:* the asset pipeline (#120, merged)

#### #161 · Product management API

The endpoints the console uses to build, publish and retire products. Part of #56.

- [ ] The endpoints above: `catalog.view` to read, `catalog.edit` to create and change, `catalog.publish` to publish, unpublish and archive
- [ ] One `PUT` saves the whole product (basics, itinerary, inclusions, prices, media, categories, visa details) in one transaction
- [ ] Slug generated from the title when omitted and unique per agency. A taken slug is a 409 that suggests a free one
- [ ] Only the agency's own assets, and only clean-scanned ones, can be attached
- [ ] Integration tests: tenant isolation, each permission, publish validation, whole-product save
- [ ] OpenAPI and the generated client updated

*Needs first:* the schema issue above

#### #162 · Tour and package screens

Where an agent builds the tours and packages they sell under their own brand. Part of #56.

- [ ] Products list with type and status filters, and search by title or destination
- [ ] Editor: basics, a day-by-day itinerary builder (add, reorder, remove days), inclusions and exclusions, price variants, images, and categories and themes
- [ ] A publish checklist showing what is missing, taken from the server's `publishProblems`
- [ ] Saving a draft never validates; publishing is a separate, confirmed action
- [ ] Editing needs `catalog.edit` and publishing needs `catalog.publish`. Without them the controls are not there, rather than disabled

*Needs first:* the API issue above. Built against a stand-in behind a port until then, like search

#### #163 · Visa listings and document checklist screens

Visas as agent-authored listings with an applicant checklist and manual fulfilment. Part of #56.

- [ ] Visa editor: visa type, entry type, processing time, validity, consular fee and service fee, and the total the customer pays
- [ ] Applicant document checklist: add, reorder, remove, mark mandatory
- [ ] Manual fulfilment is plain on the screen: the agent processes the application, and nothing is sent to an embassy

*Needs first:* the API issue above

#### #164 · Choose a product for a product-scoped markup rule

The pricing screen takes a product ID for a product-scoped rule, because there was no catalog to choose from. Part of #56.

- [ ] A product-scoped rule picks the product by name from the agency's catalog
- [ ] Only the agency's own products; archived ones are left out
- [ ] Rules already pointing at a product show its name, not its ID

*Needs first:* the API issue above

#### #18 · Asset upload pipeline

`assets` + `asset_variants`, an `IBlobStorage` port with a MinIO/S3 adapter, and background processing.

> Mostly built (74819f8): signed direct uploads, content sniffing, the scan → EXIF strip → WebP pipeline, and nothing served until scanned clean. Left: a real virus scanner — without one the pipeline stays switched off.

- [ ] Presigned direct-to-storage upload (files never proxy through the API)
- [ ] `IBlobStorage` interface so AWS/Azure stays undecided
- [ ] MIME type validated by **content sniffing**, not the file extension
- [ ] Size limits enforced server-side
- [ ] Background worker: virus scan → EXIF strip → resize variants → WebP
- [ ] `scan_status` gates public serving — nothing unscanned is served
- [ ] Tenant-scoped: agency A cannot read agency B's assets

*Needs first:* #10

### F4 · Storefront

**M2 · Queued** · 0 of 19 boxes ticked

Every agent gets a branded website: built from templates and blocks in the console, published with rollback, served on their own domain with SSL, showing their catalog to travellers. Nothing on it may mention Trips.

- **Needs:** F3, so there is something to sell.
- **Issues:** #58, #59, #60
- **Open questions it meets:** 6, 11, 20 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #58 · Website builder

The no-code branded site builder.

**Tables:** `site_templates, sites, site_versions, site_themes, site_pages, site_blocks`

- [ ] Template library.
- [ ] Logo and colour upload.
- [ ] Block-based page editing.
- [ ] About/Contact/Terms.
- [ ] Staging preview.
- [ ] Publish with rollback via versioned snapshots.
- [ ] FRD blocks publishing until a product is published — see open question 11, which may relax this for flight-only agents.

#### #59 · Custom domains, DNS verification and SSL

Each agent's site on their own domain.

**Tables:** `site_domains, site_domain_checks`

- [ ] Free subdomain provisioning.
- [ ] Custom domain with TXT/CNAME verification.
- [ ] Automatic SSL issuance and renewal at T-30 days.
- [ ] Host-header tenant resolution cached in Redis.
- [ ] Reserved-hostname denylist to stop subdomain squatting (open question 20).

#### #60 · Public storefront rendering

The Next.js traveller-facing site.

- [ ] Host-based tenant resolution.
- [ ] Per-site ISR with cache invalidation on publish.
- [ ] Template rendering from site_versions.
- [ ] Catalog browse and filter.
- [ ] Product and departure detail.
- [ ] SEO metadata, sitemap and structured data.
- [ ] Nothing on these pages may reference Trips.

### F5 · Customer commerce

**M2 · Queued** · 0 of 6 boxes ticked

Travellers buy on the agent's storefront: a cart mixing flights, buses, tours and visas, guest checkout, card payment, a magic link to manage the booking, and partial failures routed to the agent's resolution queue.

- **Needs:** F1 (the saga, extended to multi-line carts) and F4 (the storefront it runs on).
- **Issues:** #61
- **Open questions it meets:** 2, 3, 9, 10, 21 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)
- **Decided:** Open questions 1–5 must be answered before this ships: they decide who holds the money.

#### #61 · Cart, guest checkout and customer orders

The traveller's buying flow.

**Tables:** `carts, cart_items, customers`

- [ ] Guest checkout (open question 21 — no accounts in MVP).
- [ ] Mixed multi-line carts.
- [ ] Departure holds during checkout.
- [ ] Payment via Paystack.
- [ ] Magic-link 'manage my booking'.
- [ ] Partial-failure handling routing to the agent resolution queue.

### F6 · Group tours

**M2 · Queued** · 0 of 7 boxes ticked

Fixed-date departures sold by the seat, with deposits, installment plans, a waitlist and manifests — and a database that makes overselling impossible.

- **Needs:** F3 (a departure belongs to a product); F5 for travellers to buy seats.
- **Issues:** #57
- **Open questions it meets:** 12, 13 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

**Departures contract.** The console is built to it, and the backend builds to it:

```
GET  /api/v1/catalog/departures?productId=&from=      catalog.view     → Departure[]
GET  /api/v1/catalog/departures/{id}                  catalog.view     → Departure
POST /api/v1/catalog/products/{productId}/departures  catalog.edit     → Departure (201)
PUT  /api/v1/catalog/departures/{id}                  catalog.edit     → Departure; 409 on a stale version, or capacity below seats taken
POST /api/v1/catalog/departures/{id}/close            catalog.publish  → Departure: stops new bookings, existing ones stand
POST /api/v1/catalog/departures/{id}/reopen           catalog.publish  → Departure: back to the status its seats give it
POST /api/v1/catalog/departures/{id}/cancel           catalog.publish  → Departure: each paid booking on it goes to the resolution queue as a full refund (decision 12)
GET  /api/v1/catalog/departures/{id}/manifest         catalog.view     → ManifestEntry[]
GET  /api/v1/catalog/departures/{id}/waitlist         catalog.view     → WaitlistEntry[]
```

- **DepartureRequest:** `departureDate` (date), `isGroupDeparture`, `minPax`, `capacityTotal`, `cutoffDaysBefore`, `depositType` (None · Percent · Fixed), `depositPercentBasisPoints?`, `depositAmountMinor?`, `priceTiers[] {minPax, maxPax?, pricePerPaxMinor}` — contiguous from 1, the last one open-ended — and `installments[] {sequence, dueBasis (FromBooking · BeforeDeparture), dueOffsetDays, percentOfBalanceBasisPoints}` summing to 10,000, the whole balance after the deposit. With no installments the balance is due at the cutoff. A `PUT` also carries the `version` it was read at.
- **Departure** adds `id`, `productId`, `productTitle`, `currency`, `capacityReserved`, `capacityConfirmed`, `seatsLeft`, `waitlistCount`, `cutoffAt` (instant), `version`, and `status` (Open · Guaranteed · NearlyFull · SoldOut · Closed · Cancelled). The server works the status out: guaranteed once confirmed seats reach `minPax`, nearly full at 85% taken, sold out when full; closed and cancelled are the agent's.
- **ManifestEntry:** `orderReference`, `travellerName`, `paxType` (Adult · Child · Infant), `room?`, `status` (Reserved · Confirmed). **WaitlistEntry:** `id`, `name`, `paxCount`, `status` (Waiting · Offered · Converted · Expired), `joinedAt`, `offeredAt?`, `expiresAt?`.
- The table's `max_pax` stays equal to `capacity_total` until something needs them to differ. Visas have no departures.

#### #57 · Group departures, deposits and installments

Fixed-date departures sold by the seat.

**Tables:** `departures, departure_price_tiers, departure_holds, installment_plans, installment_schedule_items, departure_waitlist, pax_manifests`

- [ ] Capacity with a DB CHECK preventing oversell.
- [ ] Tiered pricing per pax count.
- [ ] Deposit + installment schedules.
- [ ] Auto status Open → Guaranteed to Run → Nearly Full → Sold Out.
- [ ] Waitlist with timed offers.
- [ ] Rooming and pax manifest.
- [ ] Concurrency test: parallel reservations never oversell.

### F7 · CRM

**M2 · Queued** · 0 of 6 boxes ticked

Leads from the storefront's trip-request widget, a pipeline from New to Won, quotes with a public accept link, follow-up tasks, and a customer record built from every inquiry, quote and booking.

- **Needs:** F2 (emails) and F4 (the widget lives on the storefront).
- **Issues:** #62

**CRM contract.** The console is built to it, and the backend builds to it. Reading needs `customer.view`; changing anything needs `customer.edit`:

```
GET  /api/v1/crm/leads                          → LeadSummary[]
GET  /api/v1/crm/leads/{id}                     → Lead: with its quotes, tasks, messages and stage history
POST /api/v1/crm/leads                          → Lead (201): a lead the agent keys in; the widget's comes from the storefront (F4)
POST /api/v1/crm/leads/{id}/stage               → Lead: {stage, reason?}; Lost needs a reason
POST /api/v1/crm/leads/{id}/quotes              → Quote (201)
GET  /api/v1/crm/quotes/{id}                    → Quote
PUT  /api/v1/crm/quotes/{id}                    → Quote; 409 unless it is still a draft
POST /api/v1/crm/quotes/{id}/send               → Quote: sets publicUrl on the agency's own domain; a New lead becomes Quoted
GET  /api/v1/crm/customers                      → CustomerSummary[]
GET  /api/v1/crm/customers/{id}                 → Customer: with their leads, quotes, bookings, tasks and messages
GET  /api/v1/crm/tasks?open=true                → Task[]
POST /api/v1/crm/tasks                          → Task (201): {title, dueAt, related {type, id}}
POST /api/v1/crm/tasks/{id}/complete            → Task
POST /api/v1/crm/communications                 → Communication (201): {channel, direction, summary, related {type, id}}
```

- **Enums:** LeadStage New · Quoted · Negotiating · Won · Lost. LeadSource TripRequestWidget · ContactForm · Manual. QuoteStatus Draft · Sent · Viewed · Accepted · Declined · Expired. Channel Email · Sms · Whatsapp · Call · Note. Direction Inbound · Outbound. RelatedType Lead · Customer · Quote.
- **LeadSummary:** `id`, `customer {id, name, email?, phone?}`, `source`, `destination`, `travelFrom?`, `travelTo?`, `adults`, `children`, `budgetMaxMinor?`, `currency`, `stage`, `ownerName?`, `createdAt`, `nextTaskDueAt?`, `quoteCount`. **Lead** adds `message`, `budgetMinMinor?`, `lostReason?`, `history[] {stage, at, byName, reason?}`, `quotes[]`, `tasks[]`, `communications[]`.
- **Quote:** `id`, `quoteNumber`, `leadId`, `customer`, `title`, `status`, `validUntil` (date), `currency`, `items[] {description, quantity, unitPriceMinor, productId?}`, `itinerary[] {dayNumber, title, description}`, `notes`, `totalMinor`, `publicUrl?`, `sentAt?`, `viewedAt?`, `respondedAt?`. Accepting happens on the public link, which is the storefront's (F4, F5).
- **Customer:** `id`, `name`, `email?`, `phone?`, `lifetimeValueMinor`, `totalBookings`, `lastActivityAt`, `openLeadCount`, plus on the full record `leads[]`, `quotes[]`, `bookings[] {reference, title, travelDate?, status, amountMinor}`, `tasks[]`, `communications[]`. Customers are never keyed in first: any inquiry, quote or booking creates or updates one (FRD §2.8 RS-1).

#### #62 · Leads, quotes and pipeline

FRD §2.8 and §2.10.

**Tables:** `customers, leads, lead_stage_history, quotes, quote_items, tasks, communications`

- [ ] Trip-request widget creating leads.
- [ ] Pipeline New → Quoted → Negotiating → Won → Lost.
- [ ] Quote builder with itinerary days and a shareable public accept link.
- [ ] Follow-up tasks with reminders.
- [ ] Communication timeline.
- [ ] Customer 360 auto-created from any inquiry, quote or booking.

## PR 3 · Milestone 3: running and charging for the platform

**Why here:** Trips needs it to operate and to earn, but nothing in it stops an agent selling — so it comes after the agent's shop, not before.

**Done when:** Milestone 3's acceptance (plan §6): a published tier's entitlements are enforced at runtime; a tier with subscribers can only be archived; a sub-agent cannot exceed its allowance or see the principal's margin; a 12-month cross-tenant report runs asynchronously and notifies; every export is logged; suspending an agent takes the storefront offline, blocks new bookings and records before and after in the audit log.

### F8 · Admin console

**M3 · Queued** · 0 of 6 boxes ticked

How Trips runs the platform: agent search and profiles, verify, suspend and terminate with an audit trail, back-office roles, and an operations dashboard.

- **Needs:** Built: KYB review (#20, #131).
- **Issues:** #66
- **Open questions it meets:** 14 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #66 · Trips back-office console

FRD §2.15 — how we operate the platform.

**Tables:** `admin_alerts, disputes`

- [ ] Agent list with search and filters.
- [ ] Agent profile view and edit with mandatory reason and audit.
- [ ] Verify, suspend and terminate with data export.
- [ ] Back-office users with role-scoped permissions (Super Admin, Support, Operations, Finance).
- [ ] Dashboard with metrics no more than 10 minutes stale, operational alerts and a top-agent leaderboard.
- [ ] Open question 14: what happens to travellers with forward bookings when an agent is suspended.

### F9 · Subscriptions and billing

**M3 · Queued** · 0 of 10 boxes ticked

Plans with entitlements the platform enforces at runtime, configured by admins, and recurring billing with dunning.

- **Needs:** F8, where admins configure tiers.
- **Issues:** #64, #65
- **Open questions it meets:** 4, 15 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #64 · Subscription tiers and entitlements

FRD §2.15 UC-1E — admin-configurable plans.

**Tables:** `subscription_tiers, tier_prices, entitlements, tier_entitlements, subscriptions, subscription_invoices, tier_change_log, subscription_migrations`

- [ ] Admin tier CRUD.
- [ ] Entitlements (max sub-agents, custom domain, transaction fee %, catalog limits, loyalty, API access).
- [ ] Pricing per interval.
- [ ] Trials and promos.
- [ ] Archive-not-delete when subscribers exist.
- [ ] Migration with advance notice.
- [ ] Full change audit.
- [ ] Entitlement enforcement middleware.
- [ ] See open question 15 on downgrades while entitlements are in use.

#### #65 · Recurring billing and dunning

Charging agents on a schedule.

- [ ] Renewals, trial expiry, dunning retries at 1/3/5/7 days, downgrade or suspend on failure, entitlement re-evaluation, subscription invoices and receipts.

### F10 · Sub-agent network

**M3 · Queued** · 0 of 7 boxes ticked

Agencies invite agents beneath them, choose what each may sell and whether they see margins, and give them wallet allowances they cannot exceed.

- **Needs:** F9 (the number of sub-agents is an entitlement).
- **Issues:** #63
- **Open questions it meets:** 6, 7, 8 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #63 · Sub-agent networks and allowances

FRD §2.7 — an agency onboards agents beneath it.

**Tables:** `sub_agent_scopes, permission_overrides, wallet_allowances`

- [ ] Invitation flow.
- [ ] Scoped permissions (which product types and suppliers a sub-agent may sell).
- [ ] Margin visibility control.
- [ ] Wallet allowances with race-free consumption.
- [ ] Freeze and revoke.
- [ ] Consolidated network reporting.
- [ ] Key test: a sub-agent with margin visibility off receives DTOs where net and markup are structurally ABSENT from the JSON, not merely null.

### F11 · Analytics and reporting

**M3 · Queued** · 0 of 11 boxes ticked

Dashboards from read models rather than live tables, and reports — synchronous for small scopes, asynchronous for large ones — with every export logged.

- **Needs:** F1, so there are bookings to count.
- **Issues:** #67, #68

#### #67 · Analytics and read models

Dashboards that do not query the OLTP tables live.

**Tables:** `fact_bookings, agg_agency_daily, agg_platform_daily, agg_supplier_daily`

- [ ] Incremental rollup every 5–10 minutes plus a nightly full rebuild.
- [ ] Agent sales/revenue/margin dashboard.
- [ ] Platform GMV and growth.
- [ ] Supplier search-to-book conversion and error rate.
- [ ] Rebuilding from source must reproduce identical numbers — analytics is derived, never authoritative.

#### #68 · Reporting and exports

FRD §2.15 UC-1C.

**Tables:** `report_definitions, report_jobs, report_schedules, report_exports_audit`

- [ ] Sync for small scopes.
- [ ] ASYNC when over 90 days or cross-tenant, notifying on completion.
- [ ] CSV/XLSX export.
- [ ] Scheduled recurring reports emailed to a distribution list.
- [ ] Drill-down from aggregate to transaction.
- [ ] EVERY export logged with actor, scope, row count and timestamp — the FRD requires this explicitly given cross-tenant sensitivity.

### F12 · Payouts, disputes and reconciliation

**M3 · Queued** · 0 of 4 boxes ticked

Money out to agents' banks, a dispute workflow with evidence, and a daily reconciliation of Paystack settlements against the ledger.

- **Needs:** F5, so travellers' card payments exist.
- **Issues:** #69
- **Open questions it meets:** 2 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #69 · Payouts, disputes and gateway reconciliation

Getting money out and keeping the books straight.

**Tables:** `payouts, agency_bank_accounts, disputes, reconciliation_runs, reconciliation_exceptions`

- [ ] Agent bank account capture and verification.
- [ ] Payout scheduling and settlement.
- [ ] Chargeback and dispute workflow with evidence.
- [ ] Daily gateway reconciliation matching Paystack settlements to our ledger, flagging mismatches.

### F13 · Loyalty and reviews

**M3 · Queued (flag only)** · 0 of 4 boxes ticked

The entitlement flag for loyalty now, so tiers can carry it. Points, redemption and reviews wait on requirements: the FRD lists both with no use case written (open question 24).

- **Needs:** F9, where entitlements live.
- **Issues:** #70
- **Open questions it meets:** 24 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #70 · Loyalty programme and reviews

FRD §1.2 lists both in scope with no use case written.

**Tables:** `loyalty_programs, loyalty_accounts, loyalty_transactions, reviews`

- [ ] Points earn and redeem.
- [ ] Verified-purchase reviews with agent moderation and platform override.
- [ ] BLOCKED on requirements — see open questions 24.
- [ ] Model the entitlement flag now, build the feature once specified.

## PR 4 · Launch readiness

**Why here:** It hardens everything above it, and the penetration test has to see the finished system.

**Done when:** Every box in F14, with the external penetration test's findings fixed, or accepted in writing.

### F14 · Security and launch readiness

**M3 · Queued** · 0 of 50 boxes ticked

What must be true before real travellers and real money: encrypted traveller documents, retention and erasure, hardened headers and cookies, scanning in CI, a load test and a penetration test.

- **Needs:** Each item names its own; the penetration test comes last.
- **Issues:** #71, #104, #105, #106, #107, #108, #109, #110
- **Open questions it meets:** 26 — see [§7 of the architecture plan](ARCHITECTURE_AND_DELIVERY_PLAN.md#7-open-questions-for-the-client)

#### #71 · Security hardening and load testing

Pre-launch readiness.

> The epic. Rate limiting (#102) and the RLS test suite (#103) are done.

- [ ] Rate limiting per user, IP and endpoint.
- [ ] RLS enforcement test suite.
- [ ] PII encryption and retention policy.
- [ ] NDPA erasure as anonymisation preserving financial and audit records.
- [ ] Penetration test remediation.
- [ ] Load test of the search endpoint at expected peak, cold and warm cache.

#### #104 · PII encryption at rest for traveller documents

- [ ] An `IFieldEncryptor` port with the key source behind it — **no cloud KMS SDK**, because the
- [ ] AES-256-GCM, a fresh IV per value, and the key id stored alongside the ciphertext so keys
- [ ] Applied through an EF Core value converter, so encryption is not something a developer has
- [ ] Plaintext never reaches logs, `audit_logs` before/after state, or `supplier_api_calls`
- [ ] A documented rotation path: re-encrypt on write under the new key id, retain old keys for
- [ ] Searching or filtering on these columns is explicitly **not** supported, and the issue says
- [ ] Test: a raw SQL `SELECT` against the column returns ciphertext, not a passport number

*Needs first:* #32, #41

#### #105 · Data retention policy and the purge job

> Built in #165: the retention table and catalogue, a daily purge that is a dry run by default and cannot touch financial or audit tables, an audit row per table, and a runbook. Left: counsel's review of the retention table (open question 26), travel dates for tours and visas, and a dry run for the supplier call log's partition job.

- [ ] A retention table in `docs/` — every table that holds personal or operational data, how
- [ ] Financial records, `audit_logs` and anything supporting them: **7 years, never touched by
- [ ] `supplier_api_calls`: 90-day hot retention by dropping monthly partitions — this is job 30
- [ ] Traveller documents purged or anonymised a defined interval after travel completes
- [ ] Implemented as a Hangfire job with a **dry-run mode** that reports what it would delete
- [ ] Every run writes an audit row: table, row count, window
- [ ] Idempotent — running it twice in a day deletes nothing extra and errors nowhere

*Needs first:* #21, #31, #32

#### #106 · NDPA erasure as anonymisation

- [ ] An erasure request is recorded, audited, and requires a stated reason
- [ ] Anonymisation replaces PII in place: name → a placeholder, email and phone → null or an
- [ ] Ledger entries, order lines, invoices and audit rows survive **and still balance** — the
- [ ] Uploaded documents removed from blob storage through `IBlobStorage`
- [ ] Irreversible: no shadow copy, no "archived" table holding what was erased
- [ ] Test: after erasure the nightly ledger integrity audit still passes and a historical invoice
- [ ] An ADR records the interpretation, who approved it, and when

*Needs first:* #21, #22, #62

#### #107 · Security headers, CORS and cookie hardening

- [ ] HSTS with a sensible `max-age`; **preload only after** custom domains are proven, since
- [ ] `Content-Security-Policy` on the storefront, `X-Content-Type-Options: nosniff`,
- [ ] Refresh token in an `HttpOnly`, `Secure`, `SameSite` cookie; the access token never in
- [ ] CORS driven by the **verified custom-domain table**, not a wildcard — `*` with credentials
- [ ] A test asserting the headers are present on both API and storefront responses, so a later

*Needs first:* #16, #59, #60

#### #108 · Dependency and secret scanning in CI

- [ ] `dotnet list package --vulnerable --include-transitive` fails the build on High or Critical
- [ ] `pnpm audit` at the same threshold
- [ ] Dependabot (or Renovate) for NuGet, pnpm **and GitHub Actions** — a compromised action is a
- [ ] CodeQL for C# and TypeScript on pull requests targeting `main`
- [ ] GitHub secret scanning with push protection enabled — this complements
- [ ] A documented triage path for the case that will definitely happen: a High advisory on a

*Needs first:* #7

#### #109 · Load test the search endpoint, cold and warm cache

- [ ] A k6 (or NBomber) scenario checked into `backend/tests/load/`, runnable locally against Docker
- [ ] Two runs reported separately: **cold** (every request reaches the supplier stub) and
- [ ] The supplier is a WireMock stub with realistic injected latency. **Never load-test against
- [ ] Reports p50/p95/p99 and error rate against the FRD §2.3 target of 5s p95, and states
- [ ] Measures what the load does to Postgres and Redis: connection pool saturation, cache hit
- [ ] Written up with the actual numbers, feeding open question 16 (is the SLA measured

*Needs first:* #33, #40

#### #110 · Penetration test — scope, execution and remediation

- [ ] A written scope: which environments, which surfaces (agent console, storefront, API, admin
- [ ] Non-production credentials and seeded test data prepared. The tester must not be able to
- [ ] **Multi-tenancy is in scope explicitly.** Give the tester two agencies and ask them to
- [ ] Findings land as **individual issues** labelled `module:security` with a severity label —
- [ ] Critical and High remediated and retested before launch; Medium and Low triaged with the
- [ ] A retest confirms the fixes actually landed

*Needs first:* #102, #103, #104, #105, #106, #107, #108, #109 (S1–S8)

## Already built

Milestone 1's foundation, all merged. Their criteria were met when they closed; the issues hold the detail.

- **Platform Foundation:** #2 Docker Compose for local infrastructure; #3 Environment variable template (.env.example); #4 .NET solution scaffold with Clean Architecture layers; #5 Architecture tests enforcing the layering rules; #6 Frontend workspace scaffold (pnpm + Turborepo); #7 CI workflow — build, test, lint; #8 EF Core, Npgsql and the migration pipeline; #9 Roslyn analyser banning decimal/float for money; #21 Platform audit log; #30 Transactional outbox and inbox; #31 MassTransit, RabbitMQ and Hangfire
- **Multi-Tenancy:** #10 Agencies schema and hierarchy; #11 ITenantContext and EF Core global query filters; #12 PostgreSQL Row-Level Security backstop
- **Identity & Access:** #13 Identity schema; #14 Agent registration with email OTP verification; #15 Login with account lockout after 5 failed attempts; #16 JWT issuance and refresh token rotation; #17 Forgot-password with a 30-minute single-use token; #49 Auth screens
- **Agency Onboarding:** #19 KYB submission and document upload; #20 Admin KYB review queue; #50 Onboarding and KYB upload screens
- **Wallet & Ledger:** #22 Double-entry ledger schema; #23 Agent wallet; #26 Wallet top-up end to end; #27 Nightly ledger integrity audit job; #51 Wallet and statement screens
- **Payments:** #24 Paystack initialize and verify; #25 Paystack webhook receiver
- **Pricing & Markup:** #28 Markup and pricing engine; #29 Immutable price quotes and order-line economics; #55 Pricing rules screen
- **Flight & Bus Booking:** #32 Supplier abstraction and schema; #33 Trips Africa flight search (international + domestic); #35 Price confirmation with SHA-512 hash validation; #39 Supplier API call auditing with redaction; #40 Redis caching for supplier search results; #52 Flight and bus search screens
- **Checkout & Orders:** #41 Order and order-line schema
- **Documents:** #47 Gapless document numbering sequences
- **Agent Console:** #48 Application shell
- **Security:** #102 Rate limiting per IP, user and agency; #103 RLS enforcement test suite
- **Persistence:** #120 reject DateTime at model-build time, not just in a test
- **Admin Console:** #131 KYB review queue screens
