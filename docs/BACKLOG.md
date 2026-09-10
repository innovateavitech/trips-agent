# Backlog

Every issue needed to build the platform, in the order it should be built.
Live board: **[github.com/innovateavitech/trips-agent/issues](https://github.com/innovateavitech/trips-agent/issues)**

This file is the map. GitHub is the source of truth for status — if the two disagree, believe GitHub.

---

## How to pick something up

1. Find an unassigned issue whose dependencies are **closed**
2. Assign it to yourself, so nobody duplicates your work
3. Branch, build, PR — see [WORKING_WITH_CLAUDE.md](WORKING_WITH_CLAUDE.md) if you are using
   Claude Code, or [CONTRIBUTING.md §4](../CONTRIBUTING.md#4-the-everyday-workflow) if not

New to the codebase? Start with a **[`good-first-issue`](https://github.com/innovateavitech/trips-agent/labels/good-first-issue)**.
These are deliberately self-contained and never touch payments, tenancy or the supplier integration —
the three places where a well-meaning mistake costs money or leaks data.

**Do not start an issue whose dependencies are still open.** You will build against
something that does not exist yet and have to redo it.

---

## Milestone 1 — Tenanted spine + live ticketing

> An agent signs up, is verified, funds a wallet, searches a real flight or bus, books it with
> markup applied, and receives a ticketed PNR and a branded invoice. If ticketing fails, the money
> is provably returned.

We attack the hardest, riskiest integration first, on purpose. If the Trips Africa money path
does not work, nothing else matters.

### Wave 1 — Foundation *(nothing else can start until these land)*

| # | Issue | Depends on |
|---|---|---|
| [#2](https://github.com/innovateavitech/trips-agent/issues/2) | Docker Compose for local infrastructure 🟢 | — |
| [#3](https://github.com/innovateavitech/trips-agent/issues/3) | `.env.example` with every config variable 🟢 | — |
| [#4](https://github.com/innovateavitech/trips-agent/issues/4) | Scaffold the .NET solution | — |
| [#5](https://github.com/innovateavitech/trips-agent/issues/5) | Architecture tests enforcing layering | #4 |
| [#6](https://github.com/innovateavitech/trips-agent/issues/6) | Scaffold the frontend workspace | — |
| [#7](https://github.com/innovateavitech/trips-agent/issues/7) | CI workflow: build, test, lint | #4, #6 |
| [#8](https://github.com/innovateavitech/trips-agent/issues/8) | EF Core, Npgsql and migrations | #4, #2 |
| [#9](https://github.com/innovateavitech/trips-agent/issues/9) | Analyser banning `decimal`/`float` for money | #4 |

### Wave 2 — Tenancy and identity

| # | Issue | Depends on |
|---|---|---|
| [#10](https://github.com/innovateavitech/trips-agent/issues/10) | Agencies schema and hierarchy | #8 |
| [#11](https://github.com/innovateavitech/trips-agent/issues/11) | `ITenantContext` + EF global query filters ⚠️ | #10 |
| [#12](https://github.com/innovateavitech/trips-agent/issues/12) | PostgreSQL RLS as a tenancy backstop ⚠️ | #11 |
| [#13](https://github.com/innovateavitech/trips-agent/issues/13) | Identity schema | #10 |
| [#14](https://github.com/innovateavitech/trips-agent/issues/14) | Registration + email OTP | #13 |
| [#15](https://github.com/innovateavitech/trips-agent/issues/15) | Login with 5-failure/15-minute lockout | #13 |
| [#16](https://github.com/innovateavitech/trips-agent/issues/16) | JWT + refresh token rotation | #14, #11 |
| [#17](https://github.com/innovateavitech/trips-agent/issues/17) | Forgot password, 30-min single-use | #13 |
| [#21](https://github.com/innovateavitech/trips-agent/issues/21) | Platform audit log | #8 |

### Wave 3 — Onboarding

| # | Issue | Depends on |
|---|---|---|
| [#18](https://github.com/innovateavitech/trips-agent/issues/18) | Asset upload pipeline | #10 |
| [#19](https://github.com/innovateavitech/trips-agent/issues/19) | KYB submission + document upload | #18, #10 |
| [#20](https://github.com/innovateavitech/trips-agent/issues/20) | Admin KYB review queue | #19, #16 |

### Wave 4 — Money 💰 *(highest care required)*

| # | Issue | Depends on |
|---|---|---|
| [#22](https://github.com/innovateavitech/trips-agent/issues/22) | Double-entry ledger schema ⚠️ | #8 |
| [#23](https://github.com/innovateavitech/trips-agent/issues/23) | Agent wallet | #22 |
| [#24](https://github.com/innovateavitech/trips-agent/issues/24) | Paystack initialize + verify | #22 |
| [#25](https://github.com/innovateavitech/trips-agent/issues/25) | Paystack webhook receiver | #24 |
| [#26](https://github.com/innovateavitech/trips-agent/issues/26) | Wallet top-up end to end | #25, #23 |
| [#27](https://github.com/innovateavitech/trips-agent/issues/27) | Nightly ledger integrity audit | #23 |
| [#28](https://github.com/innovateavitech/trips-agent/issues/28) | Markup and pricing engine | #10 |
| [#29](https://github.com/innovateavitech/trips-agent/issues/29) | Immutable price quotes ⚠️ | #28 |

### Wave 5 — Async backbone

| # | Issue | Depends on |
|---|---|---|
| [#30](https://github.com/innovateavitech/trips-agent/issues/30) | Transactional outbox and inbox | #8 |
| [#31](https://github.com/innovateavitech/trips-agent/issues/31) | MassTransit + RabbitMQ + Hangfire | #30 |

### Wave 6 — Supplier integration ⚠️ *(the riskiest code in the project)*

| # | Issue | Depends on |
|---|---|---|
| [#32](https://github.com/innovateavitech/trips-agent/issues/32) | Supplier abstraction and schema | #8 |
| [#33](https://github.com/innovateavitech/trips-agent/issues/33) | Flight search (intl + domestic) | #32 |
| [#34](https://github.com/innovateavitech/trips-agent/issues/34) | Bus/road search | #32 |
| [#35](https://github.com/innovateavitech/trips-agent/issues/35) | Price confirm + SHA-512 hash validation ⚠️ | #33, #34 |
| [#36](https://github.com/innovateavitech/trips-agent/issues/36) | **Ticket issuance, zero-retry** 🔴 | #35 |
| [#37](https://github.com/innovateavitech/trips-agent/issues/37) | Booking status poller 🔴 | #36 |
| [#38](https://github.com/innovateavitech/trips-agent/issues/38) | Ticket time limit expiry monitor | #37 |
| [#39](https://github.com/innovateavitech/trips-agent/issues/39) | Supplier API auditing with redaction | #32 |
| [#40](https://github.com/innovateavitech/trips-agent/issues/40) | Redis search caching | #33 |

### Wave 7 — Checkout and orders 🔴

| # | Issue | Depends on |
|---|---|---|
| [#41](https://github.com/innovateavitech/trips-agent/issues/41) | Order and order-line schema | #29 |
| [#42](https://github.com/innovateavitech/trips-agent/issues/42) | **Checkout saga** 🔴 | #36, #23, #41, #31 |
| [#43](https://github.com/innovateavitech/trips-agent/issues/43) | Payment reversal worker 🔴 | #37, #22 |
| [#44](https://github.com/innovateavitech/trips-agent/issues/44) | Agent resolution queue | #42 |

### Wave 8 — Documents and notifications

| # | Issue | Depends on |
|---|---|---|
| [#45](https://github.com/innovateavitech/trips-agent/issues/45) | Notification dispatcher + templates | #31 |
| [#46](https://github.com/innovateavitech/trips-agent/issues/46) | Branded invoice and voucher PDFs | #45, #41 |
| [#47](https://github.com/innovateavitech/trips-agent/issues/47) | Gapless document numbering | #46 |

### Wave 9 — Agent console frontend

| # | Issue | Depends on |
|---|---|---|
| [#48](https://github.com/innovateavitech/trips-agent/issues/48) | Agent console shell | #6 |
| [#49](https://github.com/innovateavitech/trips-agent/issues/49) | Auth screens 🟢 | #48 |
| [#50](https://github.com/innovateavitech/trips-agent/issues/50) | Onboarding + KYB upload screens | #48, #19 |
| [#51](https://github.com/innovateavitech/trips-agent/issues/51) | Wallet and statement screens | #48, #26 |
| [#52](https://github.com/innovateavitech/trips-agent/issues/52) | Flight and bus search screens | #48, #33, #34 |
| [#53](https://github.com/innovateavitech/trips-agent/issues/53) | Booking flow screens | #52, #42 |
| [#54](https://github.com/innovateavitech/trips-agent/issues/54) | Bookings list, detail, resolution queue | #48, #44 |
| [#55](https://github.com/innovateavitech/trips-agent/issues/55) | Pricing rules screen 🟢 | #48, #28 |

**Milestone 1 is done when:** a real ticket is issued against Trips Africa staging; a forced
failure proves the reversal works; a tampered hash blocks issuance; concurrent submits produce
exactly one ticket; and `wallet.balance = SUM(ledger entries)` holds after a randomised soak test.

---

## Milestone 2 — Storefront, catalog & customer commerce

These are **epics**. Break each into day-or-two-sized issues before writing code, and post the
breakdown as a comment on the epic first.

| # | Epic |
|---|---|
| [#56](https://github.com/innovateavitech/trips-agent/issues/56) | Product catalog — tours, packages and visas |
| [#57](https://github.com/innovateavitech/trips-agent/issues/57) | Group departures, deposits and installments |
| [#58](https://github.com/innovateavitech/trips-agent/issues/58) | Website builder |
| [#59](https://github.com/innovateavitech/trips-agent/issues/59) | Custom domains, DNS verification and SSL |
| [#60](https://github.com/innovateavitech/trips-agent/issues/60) | Public storefront rendering |
| [#61](https://github.com/innovateavitech/trips-agent/issues/61) | Cart, guest checkout and customer orders |
| [#62](https://github.com/innovateavitech/trips-agent/issues/62) | CRM — leads, quotes and pipeline |

---

## Milestone 3 — Network, monetisation & back-office

| # | Epic |
|---|---|
| [#63](https://github.com/innovateavitech/trips-agent/issues/63) | Sub-agent networks and allowances |
| [#64](https://github.com/innovateavitech/trips-agent/issues/64) | Subscription tiers and entitlements |
| [#65](https://github.com/innovateavitech/trips-agent/issues/65) | Recurring billing and dunning |
| [#66](https://github.com/innovateavitech/trips-agent/issues/66) | Trips admin back-office console |
| [#67](https://github.com/innovateavitech/trips-agent/issues/67) | Analytics and read models |
| [#68](https://github.com/innovateavitech/trips-agent/issues/68) | Reporting and exports |
| [#69](https://github.com/innovateavitech/trips-agent/issues/69) | Payouts, disputes and gateway reconciliation |
| [#70](https://github.com/innovateavitech/trips-agent/issues/70) | Loyalty and reviews 🚫 *blocked — no requirements written* |
| [#71](https://github.com/innovateavitech/trips-agent/issues/71) | Security hardening and load testing |

---

## Legend

| | |
|---|---|
| 🟢 | `good-first-issue` — safe and self-contained |
| ⚠️ | Touches tenancy or financial invariants. Get it reviewed carefully |
| 🔴 | On the money path. **Read [ADR-0003](adr/0003-never-retry-ticket-issuance.md) first** |
| 🚫 | Blocked — waiting on a client decision |

---

## Blocked on client answers

27 open questions are listed in [§7 of the plan](ARCHITECTURE_AND_DELIVERY_PLAN.md). Five must be
answered before Milestone 2 starts, because they change money flows rather than screens:

1. Do agents hold their own Trips Africa credentials, or does the platform transact as one merchant?
2. Who is the merchant of record for traveller payments?
3. **Who fronts the money between checkout and ticketing?** The card settles tomorrow; the ticket
   must issue now.
4. Is the platform fee added to the traveller's price, or taken from the agent's margin?
5. How do flight cancellations work? The supplier documents bus cancellation but not flight.

If an issue you are working on runs into one of these, **stop and flag it** rather than guessing.
Guessing on a money question costs more to unwind than the delay costs to wait.
