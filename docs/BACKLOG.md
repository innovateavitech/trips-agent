# Backlog

Every issue needed to build the platform, grouped by **module**.
Live board: **[github.com/innovateavitech/trips-agent/issues](https://github.com/innovateavitech/trips-agent/issues)**

This file is the map. GitHub is the source of truth for status — if the two disagree, believe GitHub.

Issues are titled `Module: What it is`, and carry a `module:` label so you can filter to one area:

```bash
gh issue list --label "module:wallet-ledger"
gh issue list --label "good-first-issue"
gh issue list --milestone "M1 — Tenanted spine + live ticketing"
```

---

## How to pick something up

1. Find an unassigned issue whose dependencies are **closed**
2. Assign it to yourself, so nobody duplicates your work
3. Branch, build, PR — see [WORKING_WITH_CLAUDE.md](WORKING_WITH_CLAUDE.md) if you are using
   Claude Code, or [CONTRIBUTING.md §4](../CONTRIBUTING.md#4-the-everyday-workflow) if not

New here? Start with a **[`good-first-issue`](https://github.com/innovateavitech/trips-agent/labels/good-first-issue)**.
These are deliberately self-contained and never touch payments, tenancy or the supplier
integration — the three places where a well-meaning mistake costs money or leaks data.

**Do not start an issue whose dependencies are still open.** You will build against something
that does not exist yet and have to redo it.

### Legend

| | |
|---|---|
| 🟢 | `good-first-issue` — safe and self-contained |
| 🧩 | Epic — break it into smaller issues first, and post the breakdown as a comment |
| ⚠️ | Touches tenancy or financial invariants. Get it reviewed carefully |
| 🔴 | On the money path. **Read [ADR-0003](adr/0003-never-retry-ticket-issuance.md) first** |
| 🚫 | Blocked — waiting on a client decision |

---

## Modules at a glance

| Module | Issues | Milestones |
|---|---|---|
| [Platform Foundation](#platform-foundation) | 12 | M1 |
| [Multi-Tenancy](#multi-tenancy) | 3 | M1 |
| [Identity & Access](#identity-access) | 6 | M1 |
| [Agency Onboarding](#agency-onboarding) | 3 | M1 |
| [Wallet & Ledger](#wallet-ledger) | 5 | M1 |
| [Payments](#payments) | 3 | M1, M3 |
| [Pricing & Markup](#pricing-markup) | 3 | M1 |
| [Flight & Bus Booking](#flight-bus-booking) | 11 | M1 |
| [Checkout & Orders](#checkout-orders) | 6 | M1, M2 |
| [Documents](#documents) | 2 | M1 |
| [Notifications](#notifications) | 1 | M1 |
| [Agent Console](#agent-console) | 1 | M1 |
| [Product Catalog](#product-catalog) | 1 | M2 |
| [Group Tours](#group-tours) | 1 | M2 |
| [Storefront](#storefront) | 3 | M2 |
| [CRM](#crm) | 1 | M2 |
| [Sub-Agent Network](#sub-agent-network) | 1 | M3 |
| [Subscriptions & Billing](#subscriptions-billing) | 2 | M3 |
| [Admin Console](#admin-console) | 1 | M3 |
| [Analytics & Reporting](#analytics-reporting) | 2 | M3 |
| [Loyalty & Reviews](#loyalty-reviews) | 1 | M3 |
| [Security](#security) | 1 | M3 |

---

## Platform Foundation

Scaffold, CI, database, messaging, assets, audit. Everything else sits on this.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#2](https://github.com/innovateavitech/trips-agent/issues/2) | Docker Compose for local infrastructure 🟢 | — | open |
| [#3](https://github.com/innovateavitech/trips-agent/issues/3) | Environment variable template (.env.example) 🟢 | — | open |
| [#4](https://github.com/innovateavitech/trips-agent/issues/4) | .NET solution scaffold with Clean Architecture layers | — | ✅ done |
| [#5](https://github.com/innovateavitech/trips-agent/issues/5) | Architecture tests enforcing the layering rules | — | ✅ done |
| [#6](https://github.com/innovateavitech/trips-agent/issues/6) | Frontend workspace scaffold (pnpm + Turborepo) | — | ✅ done |
| [#7](https://github.com/innovateavitech/trips-agent/issues/7) | CI workflow — build, test, lint | — | open |
| [#8](https://github.com/innovateavitech/trips-agent/issues/8) | EF Core, Npgsql and the migration pipeline | — | open |
| [#9](https://github.com/innovateavitech/trips-agent/issues/9) | Roslyn analyser banning decimal/float for money | — | open |
| [#18](https://github.com/innovateavitech/trips-agent/issues/18) | Asset upload pipeline | #10 | open |
| [#21](https://github.com/innovateavitech/trips-agent/issues/21) | Platform audit log | #8 | open |
| [#30](https://github.com/innovateavitech/trips-agent/issues/30) | Transactional outbox and inbox | #8 | open |
| [#31](https://github.com/innovateavitech/trips-agent/issues/31) | MassTransit, RabbitMQ and Hangfire | #30 | open |

## Multi-Tenancy

Agencies, the sub-agent hierarchy, and the isolation that keeps one agency's data away from another's.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#10](https://github.com/innovateavitech/trips-agent/issues/10) | Agencies schema and hierarchy | #8 | open |
| [#11](https://github.com/innovateavitech/trips-agent/issues/11) | ITenantContext and EF Core global query filters ⚠️ | #10 | open |
| [#12](https://github.com/innovateavitech/trips-agent/issues/12) | PostgreSQL Row-Level Security backstop ⚠️ | #11 | open |

## Identity & Access

Authentication, tokens, permissions, password flows.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#13](https://github.com/innovateavitech/trips-agent/issues/13) | Identity schema | #10 | open |
| [#14](https://github.com/innovateavitech/trips-agent/issues/14) | Agent registration with email OTP verification | #13 | open |
| [#15](https://github.com/innovateavitech/trips-agent/issues/15) | Login with account lockout after 5 failed attempts | #13 | open |
| [#16](https://github.com/innovateavitech/trips-agent/issues/16) | JWT issuance and refresh token rotation | #14, #11 | open |
| [#17](https://github.com/innovateavitech/trips-agent/issues/17) | Forgot-password with a 30-minute single-use token | #13 | open |
| [#49](https://github.com/innovateavitech/trips-agent/issues/49) | Auth screens 🟢 | #48 | open |

## Agency Onboarding

Registration through KYB verification to a live, transacting account.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#19](https://github.com/innovateavitech/trips-agent/issues/19) | KYB submission and document upload | #18, #10 | open |
| [#20](https://github.com/innovateavitech/trips-agent/issues/20) | Admin KYB review queue | #19, #16 | open |
| [#50](https://github.com/innovateavitech/trips-agent/issues/50) | Onboarding and KYB upload screens | #48, #19 | open |

## Wallet & Ledger

The double-entry ledger and the agent wallet it backs.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#22](https://github.com/innovateavitech/trips-agent/issues/22) | Double-entry ledger schema ⚠️ | #8 | open |
| [#23](https://github.com/innovateavitech/trips-agent/issues/23) | Agent wallet | #22 | open |
| [#26](https://github.com/innovateavitech/trips-agent/issues/26) | Wallet top-up end to end | #25, #23 | open |
| [#27](https://github.com/innovateavitech/trips-agent/issues/27) | Nightly ledger integrity audit job | #23 | open |
| [#51](https://github.com/innovateavitech/trips-agent/issues/51) | Wallet and statement screens | #48, #26 | open |

## Payments

Paystack, webhooks, refunds, payouts, reconciliation.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#24](https://github.com/innovateavitech/trips-agent/issues/24) | Paystack initialize and verify | #22 | open |
| [#25](https://github.com/innovateavitech/trips-agent/issues/25) | Paystack webhook receiver | #24 | open |
| [#69](https://github.com/innovateavitech/trips-agent/issues/69) | Payouts, disputes and gateway reconciliation 🧩 | — | open |

## Pricing & Markup

How a net rate becomes a sell price, and how that price is frozen.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#28](https://github.com/innovateavitech/trips-agent/issues/28) | Markup and pricing engine | #10 | open |
| [#29](https://github.com/innovateavitech/trips-agent/issues/29) | Immutable price quotes and order-line economics ⚠️ | #28 | open |
| [#55](https://github.com/innovateavitech/trips-agent/issues/55) | Pricing rules screen 🟢 | #48, #28 | open |

## Flight & Bus Booking

The Trips Africa integration: search, price confirmation, ticketing, reconciliation.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#32](https://github.com/innovateavitech/trips-agent/issues/32) | Supplier abstraction and schema | #8 | open |
| [#33](https://github.com/innovateavitech/trips-agent/issues/33) | Trips Africa flight search (international + domestic) | #32 | open |
| [#34](https://github.com/innovateavitech/trips-agent/issues/34) | Trips Africa bus/road search | #32 | open |
| [#35](https://github.com/innovateavitech/trips-agent/issues/35) | Price confirmation with SHA-512 hash validation | #33, #34 | open |
| [#36](https://github.com/innovateavitech/trips-agent/issues/36) | Ticket issuance with a zero-retry policy 🔴 | #35 | open |
| [#37](https://github.com/innovateavitech/trips-agent/issues/37) | Supplier booking status poller 🔴 | #36 | open |
| [#38](https://github.com/innovateavitech/trips-agent/issues/38) | Ticket time limit expiry monitor | #37 | open |
| [#39](https://github.com/innovateavitech/trips-agent/issues/39) | Supplier API call auditing with redaction | #32 | open |
| [#40](https://github.com/innovateavitech/trips-agent/issues/40) | Redis caching for supplier search results | #33 | open |
| [#52](https://github.com/innovateavitech/trips-agent/issues/52) | Flight and bus search screens | #48, #33, #34 | open |
| [#53](https://github.com/innovateavitech/trips-agent/issues/53) | Booking flow screens | #52, #42 | open |

## Checkout & Orders

Cart, the checkout saga, fulfilment status, and the resolution queue.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#41](https://github.com/innovateavitech/trips-agent/issues/41) | Order and order-line schema | #29 | open |
| [#42](https://github.com/innovateavitech/trips-agent/issues/42) | Checkout saga 🔴 | #36, #23, #41, #31 | open |
| [#43](https://github.com/innovateavitech/trips-agent/issues/43) | Payment reversal worker 🔴 | #37, #22 | open |
| [#44](https://github.com/innovateavitech/trips-agent/issues/44) | Agent resolution queue for failed order lines | #42 | open |
| [#54](https://github.com/innovateavitech/trips-agent/issues/54) | Bookings list, detail and resolution queue screens | #48, #44 | open |
| [#61](https://github.com/innovateavitech/trips-agent/issues/61) | Cart, guest checkout and customer orders 🧩 | — | open |

## Documents

Branded invoices and vouchers, with gapless numbering.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#46](https://github.com/innovateavitech/trips-agent/issues/46) | Branded invoice and voucher PDF generation | #45, #41 | open |
| [#47](https://github.com/innovateavitech/trips-agent/issues/47) | Gapless document numbering sequences | #46 | open |

## Notifications

Email and SMS delivery, templates, scheduled reminders.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#45](https://github.com/innovateavitech/trips-agent/issues/45) | Notification dispatcher and email templates | #31 | open |

## Agent Console

The shell every agent-facing screen lives inside.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#48](https://github.com/innovateavitech/trips-agent/issues/48) | Application shell | #6 | open |

## Product Catalog

Agent-authored tours, packages and visas.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#56](https://github.com/innovateavitech/trips-agent/issues/56) | Tours, packages and visas 🧩 | — | open |

## Group Tours

Fixed-date departures, deposits, installments, waitlists.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#57](https://github.com/innovateavitech/trips-agent/issues/57) | Group departures, deposits and installments 🧩 | — | open |

## Storefront

The site builder, custom domains, and the public branded site.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#58](https://github.com/innovateavitech/trips-agent/issues/58) | Website builder 🧩 | — | open |
| [#59](https://github.com/innovateavitech/trips-agent/issues/59) | Custom domains, DNS verification and SSL 🧩 | — | open |
| [#60](https://github.com/innovateavitech/trips-agent/issues/60) | Public storefront rendering 🧩 | — | open |

## CRM

Customers, leads, quotes, pipeline, follow-ups.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#62](https://github.com/innovateavitech/trips-agent/issues/62) | Leads, quotes and pipeline 🧩 | — | open |

## Sub-Agent Network

Agencies beneath agencies: scopes, allowances, consolidated reporting.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#63](https://github.com/innovateavitech/trips-agent/issues/63) | Sub-agent networks and allowances 🧩 | — | open |

## Subscriptions & Billing

Tiers, entitlements, recurring billing, dunning.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#64](https://github.com/innovateavitech/trips-agent/issues/64) | Subscription tiers and entitlements 🧩 | — | open |
| [#65](https://github.com/innovateavitech/trips-agent/issues/65) | Recurring billing and dunning 🧩 | — | open |

## Admin Console

Trips' own back-office.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#66](https://github.com/innovateavitech/trips-agent/issues/66) | Trips back-office console 🧩 | — | open |

## Analytics & Reporting

Read models, rollups, reports, exports.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#67](https://github.com/innovateavitech/trips-agent/issues/67) | Analytics and read models 🧩 | — | open |
| [#68](https://github.com/innovateavitech/trips-agent/issues/68) | Reporting and exports 🧩 | — | open |

## Loyalty & Reviews

Retention features.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#70](https://github.com/innovateavitech/trips-agent/issues/70) | Loyalty programme and reviews 🧩 🚫 | — | open |

## Security

Hardening, rate limiting, PII handling, load testing.

| # | Issue | Depends on | Status |
|---|---|---|---|
| [#71](https://github.com/innovateavitech/trips-agent/issues/71) | Security hardening and load testing 🧩 | — | open |

---

## Build order

Modules are for finding things. **Dependencies decide what you can actually start.**

Milestone 1 in rough order — each group needs the one above it:

1. **Platform Foundation** — #2 #3 #4 #5 #6 #7 #8 #9
2. **Multi-Tenancy** — #10 #11 #12, and **Identity & Access** #13 #14 #15 #16 #17
3. **Agency Onboarding** — #18 #19 #20, plus **Platform Foundation** #21
4. **Wallet & Ledger** #22 #23 #26 #27 and **Payments** #24 #25
5. **Pricing & Markup** #28 #29
6. **Platform Foundation** #30 #31 — the async backbone
7. **Flight & Bus Booking** #32 → #33 #34 → #35 → #36 → #37 #38, plus #39 #40
8. **Checkout & Orders** #41 → #42 → #43 #44
9. **Notifications** #45, **Documents** #46 #47
10. **Agent Console** #48, then the screens across modules: #49 #50 #51 #52 #53 #54 #55

**M1 is done when:** a real ticket is issued against Trips Africa staging; a forced failure
proves the reversal works; a tampered hash blocks issuance; concurrent submits produce exactly
one ticket; and `wallet.balance = SUM(ledger entries)` holds after a randomised soak test.

---

## Blocked on client answers

27 open questions are listed in [§7 of the plan](ARCHITECTURE_AND_DELIVERY_PLAN.md). Five must
be answered before Milestone 2 starts, because they change money flows rather than screens:

1. Do agents hold their own Trips Africa credentials, or does the platform transact as one merchant?
2. Who is the merchant of record for traveller payments?
3. **Who fronts the money between checkout and ticketing?** The card settles tomorrow; the
   ticket must issue now.
4. Is the platform fee added to the traveller's price, or taken from the agent's margin?
5. How do flight cancellations work? The supplier documents bus cancellation but not flight.

If an issue you are working on runs into one of these, **stop and flag it** rather than guessing.
Guessing on a money question costs more to unwind than the delay costs to wait.
