# Trips Agent Platform (NG) — Schema Design & Delivery Plan

## Context

`innovateavitech/trips-agent` is an **empty repository**. Everything below is greenfield.

The FRD (22 pages, `docs/FUNCTIONAL REQUIREMENT DOCUMENT.pdf`, v1.0, 7 Sep 2026) specifies a **B2B2C travel SaaS for the Nigerian market**. A travel agent signs up, is KYB-verified, picks a subscription tier, and gets:

- a workspace to search and book flights from many airlines through one interface,
- a no-code builder that publishes a **branded storefront** on a subdomain or custom domain,
- a catalog of self-authored tours, visas and group departures,
- checkout, payments, wallet, markup/commission, CRM, invoices, loyalty, sub-agent networks and analytics.

Their end customers book from the agent-branded site. The agent keeps pricing, margin and the customer relationship; Trips is invisible to them.

The problem this solves: Nigerian travel agents today sell over WhatsApp and phone, have no website, and have no single place to search, quote, book and get paid. The intended outcome is that an agent goes from signup to a live, branded, transacting storefront without hiring a developer.

### Research findings that shaped this design

I read the FRD in full and the Trips Africa API documentation. Three findings materially change the architecture:

1. **The supplier API covers flights and road/bus only.** There are no hotel, visa or tour endpoints anywhere in the docs (confirmed against `developer.tripsafrica.co/llms.txt`). FRD §1.3 lists hotels as out of scope, yet §2.3 UC-1B is a hotel booking use case — a genuine contradiction in the document. Resolved below.
2. **There are no webhooks.** Ticket issuance returns `TicketPending` as often as `TicketIssued`. The only way to learn the final outcome is to poll `GetBookingStatus`. This forces a reconciliation worker onto the critical money path rather than making it a nice-to-have.
3. **The supplier specifies exact payment-reversal conditions** (`HTTP 200` + status ∈ {0,1,11}, or `HTTP 400` + a status re-query returning {0,1,11}). The checkout state machine must be built around these rules, not around a generic happy path.

### Decisions locked with the user

| Decision | Choice |
|---|---|
| Supplier scope (MVP) | **Flights + Road/Bus** — everything the Trips API actually supports. Bus reuses the identical search→confirm→issue pipeline, so it is near-free once flights work. Hotels deferred until a supplier exists. |
| Storefront rendering | **Next.js SSR/ISR** — agent sites must be indexable by Google; that is the product's value. |
| Payment gateway | **Paystack** — behind a gateway-agnostic abstraction. |
| Visas | **Catalog product only** — agent-authored listing with manual fulfilment. Matches every use case actually written. |
| Milestone shape | **Revenue path first** — prove the hardest integration risk in M1. |
| Cloud / DB | **Not yet chosen.** Design cloud-agnostically on **PostgreSQL** (portable across AWS and Azure, no licence cost) with abstracted storage/queue/mail ports. |

### Team context

The developers on this project are **beginners**. That is a first-class design constraint, not a footnote — it shapes three things throughout this plan:

- **Documentation is a deliverable.** A README that enshrines the whole product, a contribution guide, a glossary and runbooks ship in week 1, before feature code (§5).
- **`main` is protected.** Nobody pushes to it directly; everything arrives by reviewed PR, with you as the only bypass actor (§5.3).
- **Guardrails are automated.** Architecture tests, a tenant-isolation fixture, a money-type analyser and migration safety checks turn the expensive mistakes into fast CI failures rather than things a reviewer has to catch by eye (§5.5).

---

## 1. Architecture

### 1.1 Multi-tenancy

**Shared database, `agency_id` discriminator on every tenant-scoped table**, enforced by EF Core global query filters, with PostgreSQL Row-Level Security as a defence-in-depth backstop.

Rejected alternatives and why: schema-per-tenant makes the FRD's cross-tenant admin reporting (§2.15 — platform GMV, agent leaderboards, subscription mix) require dynamic SQL over N schemas and makes migrations O(tenants); database-per-tenant additionally breaks the sub-agent hierarchy, which needs a principal agency to query across its children in one statement.

**Sub-agent hierarchy.** `agencies` carries a self-referencing `parent_agency_id` plus a materialised `path` column (`ltree`), so a principal's entire subtree is one indexed query. A sub-agent is its own `agencies` row (`type = 'sub_agent'`) rather than merely a scoped user — it needs its own wallet allowance, its own markup rules, and eventually its own storefront. It inherits branding from its parent by default.

**Tenant resolution at request time:**

- *Agent Console / Admin Console* — JWT carries `agency_id`, `root_agency_id`, `user_id`, roles. Middleware populates a scoped `ITenantContext`; query filters read from it. Platform admins carry a `platform` scope that bypasses filters via an explicit, audited `IgnoreQueryFilters` policy — never implicitly.
- *Public Storefront* — anonymous. Middleware resolves the `Host` header against `site_domains.hostname` (Redis-cached, 5-min TTL, invalidated on publish) to a `site_id` + `agency_id`. Unknown host → 404. Suspended agency → maintenance page (FRD §2.15 RS-3 requires suspension to take the site offline).

### 1.2 Stack

| Concern | Choice | Rationale |
|---|---|---|
| API | .NET 10, ASP.NET Core, Clean Architecture + MediatR | Matches the mandated backend |
| ORM | EF Core 10 + Npgsql, Dapper for reporting reads | Query filters give tenancy for free; Dapper for hot analytics paths |
| DB | PostgreSQL 16 | Portable across undecided clouds; `ltree`, `jsonb`, partitioning, RLS |
| Cache | Redis | Search cache, host→tenant map, distributed locks, rate limits |
| Recurring jobs | **Hangfire** (Postgres storage) | Dashboard + reliable cron with zero cloud lock-in |
| Event-driven / sagas | **MassTransit** over RabbitMQ | Sagas, transactional outbox, retry/circuit-breaker; transport swaps to SQS or Service Bus when the cloud is picked |
| Validation | FluentValidation | |
| PDF | QuestPDF | Invoices, vouchers, itineraries |
| Auth | ASP.NET Identity core + JWT access/refresh, Argon2id hashing | |
| Frontend | React 19 + TypeScript; Vite for consoles, **Next.js 15 App Router** for storefront | SSR/ISR only where SEO matters |
| Shared UI | Tailwind + shadcn/ui in `frontend/packages/ui` | One design system across three apps |
| API client | OpenAPI → NSwag-generated TS client | Single source of truth; CI fails if stale |
| Tests | xUnit, Testcontainers, NetArchTest, Playwright | Real Postgres/Redis/Rabbit in integration tests |

**Cloud-agnostic ports** (so the deferred infra decision costs nothing): `IBlobStorage`, `IMessageBus`, `IEmailSender`, `ISmsSender`, `ICertificateProvisioner`, `ISecretStore`. Local dev uses Docker Compose (Postgres, Redis, RabbitMQ, MinIO, Mailpit).

### 1.3 Monorepo layout

The two stacks sit in two self-contained roots, each owning its own build configuration.

```
trips-agent/
├─ backend/                   run dotnet commands from here
│  ├─ services/
│  │  ├─ TripsAgent.Api/                 REST API (agent + storefront + admin)
│  │  ├─ TripsAgent.Worker/              Hangfire + MassTransit consumers
│  │  ├─ TripsAgent.Domain/              entities, value objects, domain events
│  │  ├─ TripsAgent.Application/         use cases, validators, policies
│  │  ├─ TripsAgent.Infrastructure/      EF Core, outbox, caching, RLS
│  │  │  └─ Persistence/Migrations/      EF Core migrations + seed
│  │  ├─ TripsAgent.Contracts/           DTOs + integration events (source of TS types)
│  │  ├─ TripsAgent.Integrations.TripsAfrica/
│  │  ├─ TripsAgent.Integrations.Paystack/
│  │  ├─ TripsAgent.Integrations.Storage|Mail|Sms/
│  │  └─ TripsAgent.Documents/           QuestPDF renderers
│  ├─ tests/                  Unit | Integration | Architecture
│  ├─ TripsAgent.slnx
│  ├─ Directory.Build.props · Directory.Packages.props
│  └─ .config/dotnet-tools.json          pinned dotnet-ef
├─ frontend/                  run pnpm commands from here
│  ├─ apps/
│  │  ├─ agent-console/       React + Vite  — authenticated agent workspace
│  │  ├─ storefront/          Next.js       — multi-tenant public sites (SSR/ISR)
│  │  ├─ admin-console/       React + Vite  — Trips back-office
│  │  └─ marketing-site/      Next.js       — tripsagent.com + signup
│  ├─ packages/
│  │  └─ ui/  api-client/  contracts/  config/  utils/
│  ├─ package.json
│  └─ pnpm-workspace.yaml · turbo.json · pnpm-lock.yaml
├─ e2e/                       Playwright, drives both stacks
├─ infra/                     docker/ terraform/ k8s/
├─ scripts/                   setup.sh · check-design.sh · ef.sh
├─ docs/                      FRD, ADRs, API notes
└─ .github/workflows/
```

pnpm workspaces + Turborepo rooted at `frontend/`; one `.slnx` rooted at `backend/`; a single
CI pipeline with per-package affected-graph builds.

**Why split the roots.** Both stacks want to own the repository root — pnpm expects its
workspace file and lockfile there, MSBuild expects `Directory.Build.props` there — and mixing
them means every tool globs across the other's tree. Two roots keeps each tool's world small,
so a `pnpm install` never walks `services/` and `dotnet build` never walks `node_modules/`.

---

## 2. Data model

Grouped by bounded context; each becomes a PostgreSQL schema. `_minor` columns are `bigint` storing minor units (kobo) — **never** floating point for money. All PKs are `uuid` v7 unless noted. Every tenant-scoped table carries `agency_id uuid NOT NULL` + an index leading with it.

### 2.1 `identity` — access & authentication

| Table | Purpose & notable columns |
|---|---|
| `users` | `agency_id` (null for platform staff), `email citext UNIQUE`, `password_hash`, `status`, `email_verified_at`, `failed_login_count`, `locked_until`, `last_login_at`. `failed_login_count`/`locked_until` implement FRD's *lock after 5 failures for 15 minutes*. |
| `roles` | `agency_id` (null = system role), `name`, `scope (platform\|agency)`, `is_system` |
| `permissions` | `code` (`booking.issue`, `kyb.review`, `margin.view`), `category` |
| `role_permissions`, `user_roles` | Composite PKs; `user_roles` also carries `agency_id` so one human can hold different roles in different agencies |
| `refresh_tokens` | `token_hash`, `expires_at`, `revoked_at`, `replaced_by_id`, `ip` — rotation with reuse detection |
| `password_reset_tokens` | `token_hash`, `expires_at`, `used_at`. **30-minute, single-use** per FRD §2.1 UC-1B RS-7 |
| `otp_codes` | `channel`, `purpose`, `code_hash`, `expires_at`, `attempt_count` — email/phone verification |
| `login_attempts` | `email_normalized`, `ip`, `success`, `attempted_at` — audit trail behind the lockout; index `(email_normalized, attempted_at DESC)` |
| `user_invitations` | `agency_id` (null = back-office), `email`, `role_id`, `token_hash`, `expires_at`, `accepted_at` — serves **both** sub-agent invites (§2.7) and internal Trips users (§2.15 UC-1F) |

Login and reset responses are deliberately generic to prevent account enumeration (FRD RS-6 / RS-5).

### 2.2 `tenancy` — agencies, hierarchy, KYB

| Table | Purpose & notable columns |
|---|---|
| `agencies` | `parent_agency_id` self-FK, `path ltree` (GIST-indexed), `type (principal\|sub_agent)`, `legal_name`, `trading_name`, `slug UNIQUE`, `country_code`, `base_currency`, `timezone`, `status (pending_verification\|verified\|rejected\|suspended\|terminated)`, `verified_at`, `account_manager_user_id`, `tax_id`, `vat_rate`, `onboarding_completed_at` |
| `agency_settings` | 1:1. `supported_currencies jsonb`, `invoice_prefix`, `booking_ref_prefix`, `notification_prefs jsonb` |
| `agency_branding` | `logo_asset_id`, `primary_color`, `secondary_color`, `font_family`, `contact_address`, `social_links jsonb` — reused by storefront, invoices and emails so branding is defined once |
| `kyb_submissions` | `status (draft\|submitted\|under_review\|approved\|rejected)`, `reviewed_by`, `reviewed_at`, `rejection_reason` |
| `kyb_documents` | `doc_type`, `asset_id`, `mime_type`, `size_bytes`. CHECK constraints enforce FRD's *PDF/JPG/PNG, max 10MB* |

### 2.3 `billing` — subscriptions & entitlements

| Table | Purpose & notable columns |
|---|---|
| `subscription_tiers` | `code UNIQUE`, `name`, `customer_description`, `status (draft\|published\|archived)`, `trial_days`, `sort_order`. **Archive, never delete** — FRD forbids deleting a tier with active subscribers |
| `tier_prices` | `tier_id`, `currency`, `amount_minor`, `interval (monthly\|annual)`, `is_promotional`, `effective_from/to` — historical pricing preserved so existing subscribers keep their rate |
| `entitlements` | `code` (`max_sub_agents`, `custom_domain`, `max_catalog_listings`, `transaction_fee_pct`, `loyalty_program`, `api_access`), `value_type (bool\|int\|decimal)` |
| `tier_entitlements` | `tier_id`, `entitlement_id`, `value jsonb` |
| `subscriptions` | `agency_id`, `tier_id`, `tier_price_id`, `status (trialing\|active\|past_due\|cancelled\|expired)`, `current_period_start/end`, `trial_ends_at`, `external_ref`. Partial unique index → one active subscription per agency |
| `subscription_invoices` | `amount_minor`, `status`, `due_at`, `paid_at`, `payment_transaction_id` |
| `tier_change_log` | `actor_user_id`, `before jsonb`, `after jsonb`, `migration_policy (new_only\|migrate_existing)`, `notice_sent_at` — FRD RS-6/RS-7 require this |
| `subscription_migrations` | `from_tier_id`, `to_tier_id`, `scheduled_for`, `notified_at`, `applied_at` — honours the advance-notice rule |

### 2.4 `storefront` — site builder & domains

| Table | Purpose & notable columns |
|---|---|
| `site_templates` | `code`, `name`, `preview_image_url`, `block_schema jsonb`, `version` |
| `sites` | `agency_id UNIQUE`, `template_id`, `status`, `published_version_id`, `draft_version_id`, `primary_domain_id`, SEO fields, `analytics_ids jsonb` |
| `site_versions` | `version_no`, `status (draft\|staged\|published\|archived)`, `content_snapshot jsonb`, `theme_snapshot jsonb`, `published_at/by` — gives the FRD's *preview in staging mode* and instant rollback |
| `site_themes` | `logo_asset_id`, `colors jsonb`, `typography jsonb`, `custom_css` |
| `site_pages` | `version_id`, `slug`, `page_type (home\|about\|contact\|terms\|catalog\|custom)`, `is_system`, `meta jsonb`. UNIQUE`(version_id, slug)` |
| `site_blocks` | `page_id`, `block_type (hero\|featured_tours\|rich_text\|gallery\|contact_form\|trip_request_widget\|faq)`, `position`, `config jsonb` |
| `site_domains` | `hostname citext UNIQUE`, `type (subdomain\|custom)`, `verification_status`, `verification_method (TXT\|CNAME)`, `verification_token`, `ssl_status (none\|pending\|issued\|failed\|expired)`, `ssl_expires_at`, `last_checked_at`, `is_primary`. **Hottest lookup in the system** — Redis-cached |
| `site_domain_checks` | Per-attempt DNS results — makes "why isn't my domain working" answerable |

### 2.5 `catalog` — tours, visas, group departures

| Table | Purpose & notable columns |
|---|---|
| `products` | `agency_id`, `product_type (tour\|package\|visa)`, `slug`, `title`, `summary`, `description`, `destination_country/city`, `duration_days`, `status (draft\|published\|archived)`, `published_at`, `currency`, `base_price_minor`, `available_from`/`available_to` (dates: the booking window a tour or package needs before it can be published; dated departures with capacity stay in `departures`), `hero_asset_id`, `version`. UNIQUE`(agency_id, slug)`. `seo jsonb` is not built yet |
| `product_media` | `asset_id`, `position`, `caption` |
| `product_categories` / `product_category_map` | `type (category\|theme)` — drives storefront filtering (FRD §2.12 RS-5) |
| `tour_itinerary_days` | `day_number`, `title`, `description`, `breakfast_included`/`lunch_included`/`dinner_included`, `accommodation`. UNIQUE`(product_id, day_number)` |
| `product_inclusions` | `kind (inclusion\|exclusion)`, `text`, `position` |
| `product_price_variants` | `name` ("Double occupancy"), `pax_type (adult\|child\|infant)`, `occupancy`, `min/max_group_size`, `price_minor` — covers *variants by room type / group size / child / infant* |
| `visa_details` | 1:1 with a visa product: `visa_type`, `processing_time_days`, `validity_days`, `entry_type`, `consular_fee_minor`, `service_fee_minor` |
| `visa_document_requirements` | Applicant checklist: `label`, `is_mandatory`, `position` |
| `departures` | `departure_date`, `is_group_departure`, `min_pax`, `max_pax`, `capacity_total`, `capacity_reserved`, `capacity_confirmed`, `status (open\|guaranteed\|nearly_full\|sold_out\|closed\|cancelled)`, `deposit_type`, `deposit_amount_minor`, `cutoff_at`, `version`. CHECK `capacity_reserved + capacity_confirmed <= capacity_total` — the database, not the application, guarantees no oversell |
| `departure_price_tiers` | `min_pax`, `max_pax`, `price_per_pax_minor` — *tiered pricing per pax count* |
| `departure_holds` | `cart_id`, `pax_count`, `expires_at`, `status` — TTL seat holds during checkout; a job releases expiries |
| `installment_plans` / `installment_schedule_items` | `deposit_percent`; items carry `sequence`, `due_basis (from_booking\|before_departure)`, `due_offset_days`, `percent_of_balance` |
| `departure_waitlist` | `pax_count`, `status (waiting\|offered\|converted\|expired)`, `offered_at`, `expires_at` — FRD §2.13 RS-6 routes sold-out interest here |
| `booking_payment_schedules` / `booking_installments` | The bill one booking was given: `pax_count`, `price_per_pax_minor`, the contact to remind, and items carrying `sequence`, `label`, `due_date`, `amount_minor`, `state (pending\|paid\|cancelled)`, `last_reminder_stage`. A snapshot of the departure's terms on the day it was booked, never a view of them — job 11 reminds from it, and job 12's automatic charging waits until after the MVP |

Every catalog table carries its own `agency_id` and its own `tenant_isolation` policy — the child tables too, rather than relying on being reachable through `product_id` (ADR-0006).

Publish validation is a domain rule (`ProductPublishRules`), surfaced as a checklist in the UI rather than a DB trigger: a title, a price above zero and at least one image; for a tour or package, a booking window that has not ended; for a visa, its details and at least one document on the checklist. Visas are exempt from the window. The API returns every problem at once as `publishProblems`, so the console's checklist is never a second copy of the rules.

### 2.6 `pricing` — markup, commission, tax

| Table | Purpose & notable columns |
|---|---|
| `markup_rules` | `agency_id`, `scope (global\|product_type\|product\|supplier)`, `product_type`, `product_id`, `calc_type (percentage\|fixed)`, `percent`/`value_minor`, `min/max_markup_minor`, `priority`, `applies_to_sub_agents`, `effective_from/to` |
| `commission_rules` | Sub-agent commission splits; the platform's cut comes from the tier's `transaction_fee_pct` entitlement |
| `tax_rules` | `country_code`, `rate_percent`, `is_inclusive`, `applies_to product_type`, `effective_from` |
| `price_quotes` | **Immutable snapshot**: `net_amount_minor`, `markup_amount_minor`, `markup_rule_id`, `tax_amount_minor`, `platform_fee_minor`, `gross_amount_minor`, `fx_rate`, `breakdown jsonb`, `expires_at` |

**Resolution precedence** (deterministic, and the winning rule id is persisted on the order line so historic prices are always explainable): individual product → product type → supplier → global; higher `priority` breaks ties; the most recently `effective_from` breaks remaining ties.

Margin is only ever projected to users holding the `margin.view` permission (FRD §2.6 RS-3, §2.7 RS-3).

### 2.7 `supplier` — Trips Africa abstraction

Everything sits behind `ISupplierAdapter`; `TripsAfricaFlightAdapter` and `TripsAfricaBusAdapter` implement it. A second aggregator later requires no schema change.

| Table | Purpose & notable columns |
|---|---|
| `suppliers` | `code (trips_africa)`, `type (flight\|bus\|multi)`, `base_url`, `config jsonb` |
| `supplier_credentials` | `agency_id` **nullable** (null = platform-level), `environment (staging\|production)`, `merchant_code`, `merchant_key_encrypted`, `bearer_token_encrypted`, `rotated_at`. Nullable `agency_id` accommodates the FRD's per-agent-credentials claim while defaulting to platform credentials — see Open Question 1 |
| `search_requests` | `criteria_hash` (sha256 of normalised criteria — the cache key), `criteria jsonb`, `trip_type`, `result_count`, `latency_ms`, `error_code`. Powers the FRD's *search-to-book conversion* report |
| `search_sessions` | `supplier_session_id` (Trips `SessionId`), `gds_session_id`, `expires_at` |
| `supplier_offers` | `offer_ref`, `agent_id_ext`, `gds_id_ext`, `combination_id`, `recommendation_id`, `flight_route_index`, `base_fare_minor`, `total_fare_minor`, `raw_payload jsonb`, `expires_at` — the supplier's opaque identity tuple must round-trip exactly into price confirmation |
| `flight_segments` | `leg_index`, `segment_index`, `marketing_carrier`, `flight_number`, `origin_iata`, `destination_iata`, `departure_at`, `arrival_at`, `cabin`, `baggage_allowance`, `fare_basis` |
| `bus_segments` | `operator_name`, `departure_terminal_id`, `arrival_terminal_id`, `available_seats`, `seat_numbers jsonb`, `reservation_id_ext` |
| `supplier_bookings` | The heart of the integration. `order_line_id`, `trip_type (International\|Domestic)`, `trip_mode (Flight\|Road)`, `supplier_session_id`, `confirmation_code`, `pnr`, `ticket_time_limit`, `status`, `supplier_status_code (0\|1\|2\|3\|11\|100)`, `old_price_minor`, `new_price_minor`, `price_changed`, `hash_expected`, `hash_received`, `hash_verified`, `idempotency_key UNIQUE`, `last_polled_at`, `poll_attempts`, `next_poll_at`. Index `(status, next_poll_at)` drives the poller |
| `supplier_booking_passengers` | `passenger_type (ADT\|CHD\|INF)`, names, `birth_date`, `seat_numbers jsonb`, `ticket_number` |
| `passenger_documents` | `doc_type (DOCS\|DOCO)`, `inner_doc_type (PASSPORT\|VISA)`, `doc_number_encrypted`, issue/expiry, countries — PII, encrypted, retention-policed |
| `supplier_api_calls` | Full request/response audit with redacted headers, `latency_ms`, `correlation_id`. **Monthly partitions**, 90-day hot retention. Powers the FRD's supplier error-rate report and every "what did we actually send" dispute |
| `supplier_status_polls` | Per-poll outcome + `action_taken` — the evidence trail for any reversal |
| `supplier_fare_rules` | `rules_html`, `penalties jsonb` — must be shown before selection |

**Hash validation** (`SHA512("{MerchantKey}*{ConfirmationCode}*{NewPriceWhole}")`) runs server-side only, and for domestic/round-trip confirmations that return **arrays**, each entry is validated independently against its own `ConfirmationCode`/`NewPriceWhole`. A mismatch aborts the saga and raises a security alert.

### 2.8 `orders` — cart, orders, fulfilment

| Table | Purpose & notable columns |
|---|---|
| `carts` / `cart_items` | `session_token` supports guest checkout (FRD §2.4 RS-2). Items are polymorphic: `item_type (flight\|bus\|tour\|visa\|group_departure)`, optional `product_id` / `departure_id` / `supplier_offer_id`, `price_quote_id`, `hold_id`, `expires_at` |
| `orders` | `order_number` UNIQUE per agency, `buyer_type (customer\|agent_assisted)`, `channel (storefront\|console)`, `status (pending_payment\|paid\|partially_fulfilled\|confirmed\|partially_failed\|cancelled\|refunded)`, full money breakdown, `billing_address jsonb` |
| `order_lines` | `item_type`, `supplier_booking_id`, `title_snapshot`, `pax_breakdown jsonb`, `net_amount_minor`, `markup_amount_minor`, `markup_rule_id`, `tax_amount_minor`, `platform_fee_minor`, `gross_amount_minor`, `fulfilment_status (pending\|reserved\|confirming\|confirmed\|failed_needs_resolution\|cancelled\|refunded)`, `failure_reason`, `resolution_status (open\|in_progress\|resolved_rebooked\|resolved_refunded)`, `resolved_by`, `resolved_at`. Index `(agency_id, fulfilment_status)` **is** the agent's resolution queue required by FRD §2.4 RS-5 |
| `order_travellers` | Per-line traveller detail incl. `passport_number_encrypted` |
| `order_status_history` | `from_status`, `to_status`, `actor_type (system\|user)`, `reason` |
| `installment_schedules` / `installment_payments` | `sequence`, `due_date`, `amount_minor`, `status (scheduled\|due\|paid\|overdue\|waived)`, `reminder_sent_count`. Index `(status, due_date)` drives reminders |
| `pax_manifests` | `departure_id`, `order_line_id`, `traveller_id`, `room_assignment` — FRD §2.13 UC-1B RS-4 rooming/pax manifest |
| `cancellations` | `supplier_cancel_ref`, `penalty_amount_minor`, `refund_amount_minor`, `status` |

Prices are **snapshotted onto the order line at purchase**, never recomputed. A markup rule edited next month must not retroactively change last month's margin report.

### 2.9 `payments` — gateway, wallet, double-entry ledger

| Table | Purpose & notable columns |
|---|---|
| `payment_gateways` / `agency_gateway_configs` | `mode (platform\|agent_own)`, `subaccount_code`, `split_percent`, encrypted keys — Paystack subaccounts let agent revenue settle to the agent |
| `payment_transactions` | `reference UNIQUE`, `gateway_reference`, `purpose (order_payment\|wallet_topup\|subscription\|installment)`, `amount_minor`, `fee_minor`, `net_minor`, `status`, `authorization_code`, `idempotency_key` |
| `payment_webhook_events` | `event_id`, UNIQUE`(gateway_id, event_id)`, `signature_valid`, `payload jsonb`, `processing_status`, `attempts` — webhook idempotency and replay |
| `saved_payment_methods` | `authorization_code` (gateway token), `card_bin`, `last4`, `brand`, `exp_month/year`. **No PAN, ever** — FRD §2.4 RS-4 |
| `wallets` | `agency_id`, `currency`, `balance_minor`, `available_balance_minor`, `reserved_minor`, `status (active\|frozen)`, `version` (optimistic concurrency). UNIQUE`(agency_id, currency)` |
| `wallet_holds` | `order_id`, `amount_minor`, `status (held\|captured\|released)`, `expires_at` — reserves funds so concurrent bookings cannot overdraw |
| `wallet_allowances` | `parent_wallet_id`, `sub_agency_id`, `allowance_limit_minor`, `spent_minor`, `period`, `resets_at` — FRD §2.7 RS-4 |
| `ledger_accounts` | `account_type (agency_wallet\|platform_revenue\|supplier_payable\|customer_receivable\|gateway_clearing\|tax_payable\|refunds)` |
| `ledger_entries` | `transaction_group_id`, `direction (debit\|credit)`, `amount_minor`, `reference_type/id`, `occurred_at`. **Double-entry**: debits = credits per group, asserted nightly |
| `wallet_transactions` | Agent-facing projection: `type`, `balance_before_minor`, `balance_after_minor`, `description` — what the agent's statement renders |
| `refunds` | `type (full\|partial\|reversal)`, `destination (gateway\|wallet)`, `status`, `gateway_refund_ref`, `approved_by` |
| `payouts` / `agency_bank_accounts` | `recipient_code`, `account_number_encrypted`, settlement windows |
| `reconciliation_runs` / `reconciliation_exceptions` | `type (missing_in_ledger\|amount_mismatch\|orphan_supplier_booking)`, `expected`, `actual`, `status` |

The ledger is the source of truth for money. `wallets.balance_minor` is a cached projection, and a nightly job asserts it equals the sum of its ledger entries — if it ever diverges, that is a P1.

### 2.10 `crm`

`customers` (UNIQUE`(agency_id, email)` where email is not null, plus `lifetime_value_minor`, `total_bookings`) · `customer_documents` · `leads` (`source (trip_request_widget\|contact_form\|manual)`, destination, date range, budget range, `stage (new\|quoted\|negotiating\|won\|lost)`, `owner_user_id`, `converted_order_id`) · `lead_stage_history` · `quotes` (`quote_number`, `status (draft\|sent\|viewed\|accepted\|declined\|expired)`, `valid_until`, `public_token` for the customer-facing link) · `quote_items` · `tasks` (polymorphic `related_type/id`, `due_at`, `reminder_sent_at`) · `communications` (`channel (email\|sms\|whatsapp\|call\|note)`, `direction`).

The profile is auto-created or updated from any inquiry, quote or booking (FRD §2.8 RS-1) — customers are never manually keyed in as a precondition.

### 2.11 `documents`

`document_templates` (per `doc_type` × `product_type`, agency override of a platform default, versioned) · `generated_documents` (`document_number`, `template_version`, `asset_id`, `checksum`, `reissued_from_id`, `version_no`, `public_token`) · `document_number_sequences` (UNIQUE`(agency_id, doc_type, year)`, incremented under row lock for **gapless** numbering — a tax requirement, not a nicety).

Reissue creates a new row linked by `reissued_from_id` rather than mutating the original (FRD §2.9 RS-5).

### 2.12 `notifications`

`notification_templates` (channel × locale, versioned) · `notifications` (`recipient_type`, `channel`, `status (queued\|sending\|sent\|delivered\|failed\|bounced)`, `provider_message_id`, `attempts`, `scheduled_for`) · `notification_preferences` · `in_app_notifications`.

### 2.13 `analytics`

Read models, rebuilt by jobs — never queried live off `orders` for dashboards:

`fact_bookings` (one row per order line, denormalised with `root_agency_id` so a principal's network rolls up in one scan) · `agg_agency_daily` · `agg_platform_daily` · `agg_supplier_daily` (searches, confirms, issues, error rate, latency — the FRD's supplier-performance report) · `report_definitions` · `report_jobs` (`scope (agency\|platform)`, `status`, `result_asset_id`, `format`) · `report_schedules` · `report_exports_audit` (**every export logged with actor, scope, row count and timestamp** — FRD §2.15 UC-1C RS-6 requires this explicitly, given cross-tenant sensitivity).

### 2.14 `platform` — audit & operations

| Table | Purpose |
|---|---|
| `audit_logs` | `actor_user_id`, `actor_type`, `actor_ip`, `action`, `entity_type/id`, **`before_state jsonb`, `after_state jsonb`**, `reason`, `correlation_id`. Monthly partitions. The FRD demands before/after state for admin actions (§2.15 RS-5) |
| `outbox_messages` | Transactional outbox: written in the same transaction as the state change, dispatched at-least-once |
| `inbox_messages` | Consumer-side dedup by `message_id` |
| `idempotency_keys` | API-edge idempotency: `key UNIQUE`, `request_hash`, cached `response_body`, `locked_at` |
| `admin_alerts` | `type (pending_kyc\|gateway_error\|dispute\|reversal_required\|ticket_time_limit_breach)`, `severity`, `status` — the FRD's *operational alerts requiring action* widget |
| `disputes` | Chargebacks: `gateway_dispute_ref`, `due_by`, `evidence jsonb` |
| `feature_flags`, `system_settings` | Rollout control |
| `assets` / `asset_variants` | `storage_key`, `checksum`, `scan_status (pending\|clean\|infected)`, generated `variant` renditions |

### 2.15 `loyalty` (M3)

`loyalty_programs` · `loyalty_accounts` · `loyalty_transactions` (`type (earn\|redeem\|expire\|adjust)`) · `reviews` (`status (pending\|approved\|rejected)`, `moderated_by`).

---

## 3. Background jobs & queues — itemised

**Mechanism:** Hangfire (Postgres-backed) for cron and recurring work; MassTransit sagas + consumers over RabbitMQ for event-driven and long-running flows. Both run in `TripsAgent.Worker`, scaled separately from the API.

**Named queues:** `booking.saga` (highest priority) · `supplier.poll` · `payments.webhook` · `payments.reversal` · `notifications.email` · `notifications.sms` · `documents.render` · `media.process` · `reports.generate` (isolated pool — long-running) · `domains.provision` · `analytics.rollup` · plus a dead-letter queue per queue.

### Critical path — money and tickets

| # | Job | Trigger / cadence | Behaviour |
|---|---|---|---|
| 1 | **SupplierBookingStatusPoller** | Every 30s over `(status, next_poll_at)` | **The single most important job in the system** — there are no webhooks. Calls `GetBookingStatus`; backs off 30s→1m→2m→5m→15m→30m→1h until `ticket_time_limit` + buffer. Status 2 → `BookingTicketed`; 0/1/11 → `PaymentReversalRequired`; 100 → `admin_alerts`. Row-locked, so it is safe to run many workers |
| 2 | **CheckoutSaga** | MassTransit state machine (not cron) | Orchestrates payment → confirm → hash → issue → ticket. Detailed in §4 |
| 3 | **PaymentReversalWorker** | Consumes `PaymentReversalRequired` | Applies the supplier's exact reversal rules; refunds to gateway or credits wallet; writes balanced ledger entries; escalates to `admin_alerts` after N failures. **Never** auto-reverses without a recorded `supplier_status_polls` row as evidence |
| 4 | **TicketTimeLimitExpiryMonitor** | Every 1 min | Warns the agent at T-60m and T-15m; on expiry marks the booking failed, releases the wallet hold, flags the line for resolution |
| 5 | **OutboxDispatcher** | Every 1–5s | Publishes `outbox_messages`; at-least-once with inbox dedup |
| 6 | **CartAndHoldExpiryJob** | Every 1 min | Expires carts, releases `departure_holds` and `wallet_holds`, restores capacity |
| 7 | **LedgerIntegrityAuditJob** | Nightly | Asserts debits = credits per group and wallet balance = ledger sum; writes `reconciliation_exceptions`. A failure is P1 |
| 8 | **GatewayReconciliationJob** | Daily | Pulls Paystack settlements, matches to `payment_transactions`, flags mismatches |

### Catalog, storefront & customer lifecycle

| # | Job | Trigger / cadence | Behaviour |
|---|---|---|---|
| 9 | **DepartureStatusRecalculator** | Event-driven + nightly sweep | open → guaranteed (min_pax met) → nearly_full (≥85%) → sold_out; on sold_out routes interest to the waitlist; on freed capacity offers the next entry |
| 10 | **WaitlistOfferExpiryJob** | Every 5 min | Expires unclaimed offers, rolls to the next person |
| 11 | **InstallmentReminderJob** | Daily | Reminders at T-7/T-3/T-1 and on overdue; past grace → flag line + notify agent |
| 12 | **InstallmentAutoChargeJob** | Daily | Charges saved authorisations where the customer opted in |
| 13 | **DnsVerificationJob** | 5 min → hourly backoff, abandon at 7 days | Resolves TXT/CNAME; on success enqueues SSL issuance |
| 14 | **SslProvisioningJob** | On domain verified + daily renewal check | ACME/ACM issuance, validation polling, renewal at T-30 days |
| 15 | **StorefrontCacheInvalidator** | On site or product publish | Purges CDN + Next.js ISR tags for the affected host |
| 16 | **SearchCacheWarmer/Evictor** | Scheduled + on error spike | Redis 3–5 min TTL keyed on `criteria_hash`; warms high-traffic NG routes (LOS-ABV, LOS-LHR) |
| 17 | **AbandonedCartNudge** | Hourly | Carts idle > 2h with contact details |
| 18 | **QuoteExpiryJob** | Daily | Expires quotes past `valid_until`, notifies the agent |
| 19 | **AgentTaskReminderJob** | Every 15 min | CRM follow-ups due → in-app + email (FRD §2.8 RS-4) |

### Platform, billing & reporting

| # | Job | Trigger / cadence | Behaviour |
|---|---|---|---|
| 20 | **NotificationDispatcher** | Consumes notification queues | Per-channel providers, exponential retry, dead-letter after 5, bounce/complaint handling |
| 21 | **DocumentRenderWorker** | Consumes `InvoiceRequested` / `VoucherRequested` | QuestPDF render → blob → email |
| 22 | **MediaProcessingWorker** | On asset upload | Virus scan, EXIF strip, resize variants, WebP |
| 23 | **AnalyticsRollupJob** | Every 5–10 min incremental + nightly full rebuild | Populates `fact_bookings` and the `agg_*` tables. Cadence is set by the FRD's *metrics refresh at least every 10 minutes* |
| 24 | **ReportGenerationWorker** | Consumes `reports.generate` | Runs >90-day or cross-tenant reports async, writes CSV/XLSX, notifies requester, writes `report_exports_audit` |
| 25 | **ScheduledReportDispatcher** | Cron per `report_schedules` | Enqueues jobs, emails the distribution list |
| 26 | **SubscriptionBillingJob** | Daily | Renewals, trial expiry, dunning (retry 1/3/5/7 days), downgrade/suspend on failure, entitlement re-evaluation |
| 27 | **SubscriptionMigrationJob** | Daily | Applies scheduled tier migrations after the notice period |
| 28 | **KybReviewSlaMonitor** | Daily | KYB pending > 48h → `admin_alerts` |
| 29 | **LoginSecurityJob** | Every 15 min | Clears expired lockouts, prunes `login_attempts`, revokes expired refresh/reset tokens |
| 30 | **SupplierApiCallPruner** | Monthly | Drops `supplier_api_calls` partitions past retention |

---

## 4. The money path — checkout saga

```
Created → PaymentAuthorising → PaymentCaptured → SupplierConfirming
        → PriceValidated (hash) → Issuing → TicketPending → Ticketed
                                                ↘ PartiallyFailed → AgentResolution
                                                ↘ ReversalPending → Reversed
```

Rules that make this safe:

- **Idempotency at three layers.** `idempotency_keys` at the API edge; `supplier_bookings.idempotency_key UNIQUE` at the persistence layer; a Redis distributed lock keyed on cart/order around the issue call. Double-clicking "Pay" must never issue two tickets.
- **Outbox everywhere.** Every state change writes its domain events to `outbox_messages` inside the same transaction. No dual-write between DB and broker.
- **Hash gate.** Issue is unreachable unless `hash_verified = true` for **every** confirmation in the response array. A mismatch aborts and raises a security alert.
- **Price-change gate.** `OldPrice ≠ NewPrice` pauses the saga and requires explicit re-consent from the agent or customer within `ticket_time_limit` (FRD §2.3 RS-4).
- **Wallet ordering.** For agent-funded bookings, a `wallet_hold` is placed *before* supplier confirm and captured only on `Ticketed`. Insufficient balance fails fast, before any supplier call.
- **Post-payment failure is a first-class state, not an exception.** The line becomes `failed_needs_resolution` with `resolution_status = open`, the agent's resolution queue lights up, and the customer is notified — exactly as FRD §2.4 RS-5 specifies. It is never silently rolled back, and it never auto-refunds; the agent chooses retry, substitute or refund.
- **Reversal is evidence-based.** Automatic reversal fires only on the supplier's documented conditions, and only with a persisted `supplier_status_polls` row justifying it.

### The rule that prevents the worst bug we could ship

**A timeout on the supplier's `issue` endpoint is an *unknown*, never a failure.**

`/api/v2/ticketing/issue` is not idempotent and the supplier gives us no idempotency key. If it times out, the ticket may well have been issued. Treating that as a failure and retrying issues a second ticket the customer did not buy and we cannot easily refund.

The only legal response to a timeout is `GetBookingStatus`. Concretely:

- **Zero retries on `issue`.** The Polly pipeline for that one call has retry disabled — deliberately, with a comment explaining why. Search gets a 20s timeout with retries; issue gets 45s with none.
- On timeout, the saga moves to `IssueOutcomeUnknown` and hands off to the poller. It never re-issues.
- If the worker is killed mid-`issue`, restart recovery goes through `GetBookingStatus`, never through re-issuing.

**Four layers guard against double-ticketing**, and the durable one is a database constraint rather than a lock, because locks do not survive a process dying:

1. API-edge idempotency key
2. Redis distributed lock on the order line
3. A saga state that forbids re-entering `Issuing`
4. `UNIQUE (order_line_id)` on `supplier_bookings` ← the one that actually holds

### Integrity enforced by the database, not by convention

Three invariants are too important to leave to code review:

- **Order-line economics are immutable.** A `BEFORE UPDATE` trigger raises an exception on any attempt to change `net_amount_minor`, `markup_amount_minor`, `tax_amount_minor` or `gross_amount_minor` after `placed_at` is set. Historic margin cannot be rewritten, even by a bug.
- **The ledger is append-only.** `REVOKE UPDATE, DELETE ON ledger_entries` from the application role. Corrections are new reversing entries, never edits.
- **The ledger always balances.** A deferred constraint trigger asserts `SUM(debits) = SUM(credits)` per `transaction_group_id` at commit time. An unbalanced transaction cannot be committed at all.

---

## 5. Documentation & repository governance

The team working on this is **early in their careers**. That changes what "done" means for the foundation: the repo has to teach, and the process has to make the expensive mistakes hard to commit. Everything in this section ships in **Milestone 1, week 1** — before feature work starts.

The guiding principle: *a new developer should be able to clone the repo on Monday morning and open their first PR by Monday afternoon without asking anyone a question.*

### 5.1 `README.md` — the product, enshrined

The root README is the single source of truth for what this product **is**, not just how to run it. Sections:

1. **What the Trips Agent Platform is** — the B2B2C model explained in plain language: Trips builds the platform → travel agents are our customers → the agents' travellers are the end customers → Trips stays invisible to those travellers. A diagram of that chain.
2. **Who uses it** — the five personas from FRD §1.4 (Independent Agent, Agency Owner, Sub-Agent, End Customer, Trips Admin) and what each one is trying to get done.
3. **Glossary** — non-negotiable for this domain, because the words are not guessable: *Agent, Principal Agent, Sub-Agent, Storefront, White-Label, Markup, Net Rate, Sell Price, Wallet, KYB, PNR, GDS, Ticket Time Limit, Departure, Group Departure, Manifest, Entitlement, Tenant*. Every term links to where it lives in the code.
4. **How the system fits together** — a diagram of Agent Console / Storefront / Admin Console → API → Domain → Supplier & Payment adapters → Postgres / Redis / queue, plus the request lifecycle for the two flows that matter most (an agent booking a flight; a traveller checking out on a branded site).
5. **Repository map** — every top-level folder, one line each, saying what belongs there and what does *not*.
6. **Tech stack and why** — each choice with its one-sentence justification, so nobody has to relitigate it in a PR.
7. **Local setup** — exact copy-pasteable commands, expected output at each step, and a troubleshooting table for the five failures we predict (Docker not running, port already in use, migrations not applied, missing `.env`, stale generated API client).
8. **How to run things** — API, each frontend, the worker, migrations, tests, seed data, the Hangfire dashboard.
9. **Test accounts and seed data** — the pre-seeded agent, sub-agent, admin and customer logins, and the Paystack/Trips Africa staging sandbox details.
10. **Where to find things** — a "I want to change X, which file do I open?" table. This single section will save more time than any other.
11. **Links out** — to `CONTRIBUTING.md`, the FRD, ADRs, and this plan.

Plus a short `README.md` in each app and service folder covering that unit's responsibility and boundaries.

### 5.2 `CONTRIBUTING.md` — how we work

Written for someone who has never worked on a team repo before. Every instruction is a literal command, not a description of one.

**Branching model — trunk-based, deliberately simple.** One long-lived branch (`main`), short-lived branches off it, merged by PR. No `develop`, no `release/*`, no GitFlow — the extra branches cause more beginner mistakes than they prevent, and releases are handled by tags.

```
main                    protected, always deployable, nobody pushes to it
  ├─ feat/M1-wallet-topup
  ├─ fix/login-lockout-counter
  └─ chore/upgrade-efcore
```

Branch naming: `<type>/<milestone-or-issue>-<short-slug>`, kebab-case, lowercase.

**Commit convention — Conventional Commits, enforced not suggested.**

```
<type>(<scope>): <subject>

feat(wallet): credit agent wallet on successful Paystack top-up
fix(auth): reset failed_login_count after a successful login
docs(readme): add local setup troubleshooting table
```

Types: `feat` · `fix` · `docs` · `refactor` · `test` · `chore` · `perf` · `build` · `ci`. Scopes match the repo map (`auth`, `wallet`, `supplier`, `catalog`, `storefront`, `admin`, `ui`, `db`). Enforced by **commitlint + husky** so a malformed message is rejected locally, before it ever reaches a PR — the failure arrives in two seconds instead of twenty minutes.

**The everyday workflow**, spelled out end to end:

```bash
git checkout main && git pull origin main       # 1. always start from fresh main
git checkout -b feat/M1-wallet-topup            # 2. branch
# ... work ...
pnpm lint && pnpm test && dotnet test           # 3. green locally BEFORE pushing
git add -p                                      # 4. stage deliberately, review your own diff
git commit -m "feat(wallet): credit wallet on top-up"
git push -u origin feat/M1-wallet-topup         # 5. push the branch, never main
gh pr create --fill                             # 6. open the PR
```

Plus the situations beginners actually hit, each with the exact recovery commands: *"main moved while I was working"* (rebase, and how to resolve a conflict), *"I committed to main by accident"*, *"I committed the wrong file"*, *"I committed a secret"* (stop, rotate, tell someone — do not just amend), *"my PR has 40 commits"* (that's fine, we squash).

**Definition of Done** — a PR is ready when: it does one thing; tests cover the new behaviour; it runs locally; no commented-out code, no stray `Console.WriteLine`/`console.log`; no secrets, and any new config is documented in `.env.example`; new tenant-scoped tables carry `agency_id` and a filter; money uses minor-unit integers; the README or an ADR is updated if a decision changed.

**Code review etiquette, both directions.** For authors: keep PRs under ~400 changed lines, write a description that says *why*, respond to every comment. For reviewers: review within one working day, distinguish blocking from non-blocking (prefix nits with `nit:`), explain reasoning rather than issuing instructions, and approve when it is good enough rather than perfect. Reviews critique code, never people — stated explicitly, because juniors need to be told that out loud.

**Escalation** — how long to stay stuck before asking (suggested: 30 minutes), and where to ask.

### 5.3 Branch protection — nobody pushes to `main`

Configured as a **GitHub ruleset** on `innovateavitech/trips-agent` targeting `main`:

| Rule | Setting |
|---|---|
| Require a pull request before merging | ✅ |
| Required approvals | 1 |
| Dismiss stale approvals on new commits | ✅ |
| Require review from Code Owners | ✅ |
| Require conversation resolution before merging | ✅ |
| Require status checks to pass | ✅ — see list below |
| Require branches to be up to date before merging | ✅ |
| Require linear history | ✅ (pairs with squash-merge) |
| Block force pushes | ✅ |
| Restrict deletions | ✅ |
| Require signed commits | ⬜ optional — recommend deferring; it trips beginners up on day one |

**Bypass list: you (repo/org admin) only.** In ruleset terms, add your account as the sole bypass actor. This gives you the emergency escape hatch you asked for while everyone else — including future senior hires — goes through a PR. If you would rather have *no* exceptions, leave the bypass list empty; the ruleset works either way and can be changed later without touching code.

**Required status checks** (each must be a real, fast CI job before it is marked required):

- `build-api` — `dotnet build` with warnings as errors
- `test-api` — `dotnet test` including Testcontainers integration tests
- `build-web` — Turborepo build across all four frontends
- `test-web` — Vitest + React Testing Library
- `lint` — ESLint + Prettier + `dotnet format --verify-no-changes`
- `migrations` — asserts EF migrations apply cleanly to an empty database and that no model change is left un-migrated
- `api-client-freshness` — regenerates the TypeScript client from OpenAPI and fails if it differs from what is committed
- `commitlint` — PR title follows Conventional Commits (the squash-merge commit message comes from the PR title)

**Merge strategy: squash-and-merge only.** Rebase and standard merge are disabled in repo settings. This is the single highest-leverage setting for a beginner team — messy work-in-progress history is fine on the branch and disappears on merge, `main` stays one clean commit per change, and reverting is trivial.

**Repo settings** — auto-delete head branches after merge; disable "Allow merge commits" and "Allow rebase merging"; default branch `main`.

### 5.4 Supporting files

| File | Purpose |
|---|---|
| `.github/pull_request_template.md` | What changed · why · how to test it · screenshots for UI · linked issue · the DoD checklist as tick-boxes |
| `.github/ISSUE_TEMPLATE/bug_report.md` · `feature_request.md` · `task.md` | Consistent, well-formed issues |
| `CODEOWNERS` | Routes reviews automatically — you own `/backend/services/**/Payments/`, the persistence layer, `.github/`, and anything touching the ledger or supplier adapters, so money-path and schema changes always reach you |
| `.env.example` | Every variable, with a comment and a safe dummy value. Real secrets never enter the repo |
| `docs/adr/` | Architecture Decision Records — one short file per significant decision (why Postgres, why Hangfire + MassTransit, why squash-merge). Teaches the team that decisions have reasons and can be revisited |
| `docs/onboarding.md` | A guided first week: read these three things, run the app, pick a `good-first-issue`, ship it |
| `docs/domain-glossary.md` | The expanded glossary, with the FRD section reference for each term |
| `docs/runbooks/` | What to do when: a ticket is stuck in `TicketPending`, a reversal fails, wallet balance ≠ ledger, SSL provisioning fails |
| `.editorconfig`, `.gitattributes` | Consistent formatting and line endings across macOS/Windows |
| `.husky/` | `pre-commit` → lint-staged formats and lints only staged files; `commit-msg` → commitlint |
| `CHANGELOG.md` | Generated from conventional commits at each milestone tag |

### 5.5 Guardrails that catch beginner mistakes automatically

Process documents are ignored under pressure; CI is not. Each of these turns a class of expensive mistake into a fast, self-explanatory failure:

- **Architecture tests** (NetArchTest) — the Domain layer cannot reference Infrastructure; Controllers cannot reference `DbContext` directly. Layering violations fail the build instead of quietly accumulating.
- **A tenant-isolation test fixture** that every tenant-scoped feature must pass — proves agency A cannot read agency B's rows.
- **A money-type analyser** — a Roslyn rule failing the build on `decimal`/`double` used for currency amounts outside the designated conversion helpers.
- **Migration safety check** — flags destructive operations (column drops, type narrowing) in CI so they get a deliberate second look.
- **Secret scanning** — GitHub secret scanning plus push protection enabled on the repo.
- **`good-first-issue` labels** — a standing backlog of genuinely small, well-scoped tasks so a new joiner's first PR is a success rather than a two-week ordeal.

### 5.6 Where this lands in the schedule

`README.md`, `CONTRIBUTING.md`, branch protection, PR/issue templates, CODEOWNERS, husky/commitlint and the CI skeleton are **Milestone 1, week 1** — the first PR into the repo, before any feature code. They are then living documents: the README's glossary and "where to find things" table grow with each milestone, ADRs are added as decisions are made, and runbooks are written as each background job goes live.

---

## 6. Milestones

### Milestone 1 — Tenanted spine + live ticketing

*Outcome:* a verified agent logs in, funds a wallet, searches a real flight or bus on Trips Africa staging, books it with markup applied, receives a ticketed PNR and a branded invoice — and if ticketing fails, the money is provably reversed.

This milestone deliberately attacks the highest-risk integration first.

**Week 1 — repo foundation (§5).** `README.md` with the product, personas, glossary, architecture and local setup · `CONTRIBUTING.md` with the branching, commit and PR workflow · branch protection on `main` with you as the only bypass actor · squash-merge-only · PR/issue templates · `CODEOWNERS` · husky + commitlint · `.env.example` · Docker Compose · CI skeleton with the required status checks · `docs/onboarding.md` and the first `good-first-issue` backlog. **No feature PR merges before this is in place.**

**Backend** — solution scaffold + Clean Architecture + arch tests + CI/CD · Postgres, EF Core, migrations, seed · multi-tenancy (`ITenantContext`, query filters, RLS) · identity (registration, OTP, login with 5-fail/15-min lockout, JWT + refresh rotation, 30-min single-use reset, RBAC) · KYB submission, upload, admin approve/reject · agency settings + branding · wallet + double-entry ledger + holds · Paystack init/verify/webhook + wallet top-up · markup engine + immutable price snapshots · `ISupplierAdapter` + Trips Africa flight & bus adapters (search, confirm, **SHA-512 hash validation incl. arrays**, issue, status, rules, bus cancel) · Redis search cache · `supplier_api_calls` audit · checkout saga + outbox + idempotency + distributed locks · invoice/voucher PDF + email · audit log.

**Frontend (agent-console)** — auth flows · onboarding wizard + KYB upload · dashboard shell · wallet + statement · flight/bus search → results → travellers → review → confirm → ticketed · bookings list/detail · resolution queue · pricing rules · settings & branding.

**Migrations:** `identity`, `tenancy`, `payments`, `pricing`, `supplier`, `orders`, `documents`, `notifications`, `platform`.

**Jobs introduced:** 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 29.

**Acceptance** — a ticket is issued end-to-end against Trips staging; a forced-failure scenario proves correct reversal; a tampered hash blocks issuance; concurrent submits produce exactly one ticket; `wallet.balance = SUM(ledger entries)` holds after a randomised transaction soak. **Plus:** a developer who has never seen the repo can clone it, follow the README, run the full stack, and open a passing PR without asking a question; a direct push to `main` from a non-admin account is rejected by GitHub.

### Milestone 2 — Storefront, catalog & customer commerce

*Outcome:* an agent builds and publishes a branded site on a custom domain with valid SSL, lists tours, visas and group departures, and a real customer browses, requests a trip, receives a quote, and checks out — including a group-tour deposit with a scheduled installment plan — landing in the agent's CRM.

**Backend** — asset pipeline + CDN · catalog (products, itineraries, inclusions, media, price variants, categories, visa details, publish validation) · departures, capacity + holds, tiered pricing, deposits, installment plans, waitlist, auto status transitions · site builder (templates, versions, pages, blocks, themes, staging/publish) · domains (subdomain provisioning, DNS verification, SSL issuance + renewal) · host-resolved anonymous storefront API with aggressive caching · cart + guest checkout + customer accounts · checkout saga extended to mixed multi-line carts with partial-failure handling · installments + reminders + auto-charge · CRM (customers, leads from the trip-request widget, pipeline, quotes with a public accept link, tasks, communications) · vouchers, itineraries, quote PDFs, gapless numbering.

**Frontend** — *storefront (Next.js)*: Host-based tenant resolution, per-site ISR, template rendering, catalog browse/filter, product and departure detail, trip-request widget, cart, checkout, order tracking, customer account. *agent-console*: catalog CRUD + itinerary builder + media manager, departures + manifest/rooming, site builder (block editor, live preview, publish), domain wizard, CRM inbox/pipeline/quote builder, tasks.

**Migrations:** `catalog`, `storefront`, `crm`, plus `orders` extensions (installments, manifests).

**Jobs introduced:** 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19.

**Acceptance** — a custom domain serves the site over valid SSL; publish is blocked until at least one product is published (FRD §2.11 RS-6); concurrent reservations never oversell a departure; a group deposit creates a correct installment schedule; a lead → quote → paid order is traceable end-to-end; a post-payment supplier failure appears in the resolution queue and notifies the customer.

### Milestone 3 — Network, monetisation, intelligence & back-office

*Outcome:* Trips operates the platform — configures tiers, verifies and suspends agents, runs cross-tenant reports; agencies run sub-agent networks with allowances and scoped permissions; everyone gets analytics; loyalty and reviews drive retention.

**Backend** — sub-agent hierarchy (invitation, scoped permissions, wallet allowances, consolidated reporting, freeze/revoke) · subscription tiers (admin CRUD, entitlements, pricing, trials, promos, archive-not-delete, migration with notice, change audit) · entitlement-enforcement middleware · recurring billing + dunning · admin console API (agent search, profile view/edit with mandatory reason + audit, verify/suspend/terminate + data export, back-office users with role-scoped permissions) · analytics rollups + agent dashboard + admin dashboard + leaderboard + operational alerts · reporting (sync vs async threshold, CSV/XLSX, scheduled reports, export audit) · loyalty + reviews + moderation · disputes, payouts, gateway reconciliation · hardening (rate limiting, RLS enforcement tests, PII encryption + retention, pen-test remediation, load test).

**Frontend** — *agent-console*: sub-agent management, permissions matrix, allowances, network performance, subscription & billing, reports + export, loyalty config, review moderation. *admin-console*: dashboard, agent management, KYB queue, tier builder, back-office user management, reports & analytics, disputes, audit log viewer, feature flags. *storefront*: loyalty display, reviews.

**Migrations:** `billing`, `analytics`, `loyalty`, plus `platform` extensions (disputes, flags).

**Jobs introduced:** 8, 23, 24, 25, 26, 27, 28, 30.

**Acceptance** — a tier is published and its entitlements are enforced at runtime; a tier with active subscribers cannot be deleted, only archived; a sub-agent cannot exceed its allowance or view the principal's margin; a 12-month cross-tenant report runs asynchronously and notifies on completion; every export is logged; suspending an agent takes the storefront offline, blocks new bookings, and records before/after state in the audit log.

---

## 7. Open questions for the client

Ordered by blast radius. **Nothing here blocks starting Milestone 1**, but items 1–5 must be answered before Milestone 2 begins, because they change money flows rather than screens.

### Blocking before M2 — these are commercial, not technical

1. **Who holds the Trips Africa credentials?** FRD §2.3 says *"the agent's Trips Africa API credentials are active"*, implying per-agent merchant accounts. The API has a single platform-level MerchantKey/MerchantCode and no per-agent provisioning endpoint. These are **different businesses**: per-agent means the agent owns the supplier relationship and its credit risk and we are pure software; platform-level means *we* are the merchant, we carry the supplier float and counterparty risk, and we reconcile one supplier account across thousands of tenants. *(Schema supports both via nullable `supplier_credentials.agency_id`.)*

2. **Who is the merchant of record for customer payments?** Platform collects and settles into the agent's wallet, or the agent's own Paystack subaccount collects directly? Determines PCI scope, chargeback liability, working capital, and whether we handle third-party funds in a way that attracts CBN attention. **Recommendation:** platform as merchant of record with Paystack split settlement to agent subaccounts. Needs legal sign-off, not just product sign-off.

3. **Who fronts the money between checkout and ticketing?** This is the sharpest gap in the FRD. §2.3 requires the agent to have *sufficient wallet balance* to book; §2.4 has the traveller paying by card at checkout on the storefront. But the card settles T+1 while the ticket must issue **now**, inside the `TicketTimeLimit`. Someone funds that window. Options: (a) agents pre-fund their wallet and it is debited at issue, with the customer's payment topping it back up — safe for us, hard on small agents' cash flow; (b) the platform underwrites the gap — we take credit risk on every booking; (c) instant-settlement rails only. **Recommendation: (a) as default**, with (b) available per-agent as an entitlement-gated facility. Note FRD §1.3 puts credit management out of scope, which argues for (a).

4. **Is the platform transaction fee added to the traveller's price, or deducted from the agent's margin?** FRD §2.15 RS-3 defines it as a tier entitlement but never says where it lands. **Recommendation: deducted from the agent's margin** — adding it to the sell price would make agents on cheaper tiers visibly more expensive to their own customers, breaking the product's core promise. The schema is built this way, and changing it later means re-deriving every historical margin.

5. **Flight cancellation.** The docs index lists "Cancel Flight" pages, but only **bus** cancellation is actually specified. If flight cancellations are an offline process with Trips Africa, that changes the UX from a button to a queued human request, and changes the refund SLA. Needs confirmation plus their void window and penalty rules.

### High impact

6. **Sub-agent branding.** FRD §2.7 RS-5 says sub-agents are *"scoped entirely within the principal agent's brand"*, then says they *"never see the Trips brand or the principal agent's"*. These conflict. **Recommendation:** one site per tenant, sub-agents sell under the principal's brand.
7. **Hierarchy depth.** Is principal → sub-agent enough, or are franchise networks (depth 3+) needed? **Recommendation: cap at depth 2 for MVP.** `ltree` makes lifting the cap a dropped constraint, but console UI, allowance semantics and reporting all get materially harder deeper.
8. **Wallet allowance semantics.** FRD §2.7 RS-4 says "sets a wallet allowance" without defining it — hard cap on drawing from the principal's wallet, a separately funded float, or a revolving periodic limit? **Recommendation: hard cap**, given credit management is out of scope.
9. **Price-change re-consent on an unattended storefront.** FRD §2.3 RS-4 requires reconfirmation when the price moves. Sensible for an agent at a desk, hostile for a traveller at 1 a.m. **Recommendation:** an agent-configurable tolerance band (default 0 = always ask); increases above the band always require consent, decreases pass through automatically.
10. **`TicketTimeLimit` vs Nigerian payment rails.** Bank transfer and USSD settle in minutes, not seconds. If the TTL lapses mid-payment we have taken money for a hold that no longer exists. **Recommendation:** hide non-instant payment methods on supplier-sourced lines when the TTL is under 30 minutes, and show a live countdown at checkout. Restricts payment choice, so needs product sign-off.
11. **The publish gate blocks flight-only agents.** FRD §2.11 RS-6 forbids publishing a site until a tour, visa or group tour is published — so an agent whose entire business is flight ticketing can never go live. **Recommendation:** relax to *"at least one published product **or** flight search enabled on the site"*.
12. **Group departure cancellation.** The FRD defines the min-pax mechanism but never says what happens when min pax is missed. Are deposits fully refunded, partly, or at the agent's discretion? This is consumer-protection exposure, not a preference.
13. **Installment default.** FRD §2.13 schedules reminders and stops. What happens when an installment is never paid? **Recommendation: never auto-cancel** — flag for the agent at T+7 with a suggested action. Automatically releasing a customer's paid-deposit spot is the most expensive support incident this product could generate.
14. **Suspended agents with live forward bookings.** FRD §2.15 RS-3 takes the site offline and blocks new bookings, and says nothing about travellers who already paid for travel next month. Who services them? Are their documents still reachable? Needs writing down before the first suspension, not after.
15. **Subscription downgrade with entitlements in use.** What happens when an agent downgrades below `custom_domain` while a domain is live and taking traffic, or below `max_sub_agents` while sub-agents exist? **Recommendation:** grandfather existing usage, block new usage, 30-day remediation notice. Silently dropping a live custom domain would take an agent's business offline.

### Medium

16. **Search latency SLA.** FRD §2.3 requires 5s for 95% of queries; live GDS search routinely exceeds that. Confirm the SLA is measured post-cache, or renegotiate against measured supplier latency.
17. **Multi-currency and FX risk.** Who bears it, and which rate is authoritative (CBN official, parallel, gateway, or a commercial feed)? **Recommendation:** MVP sells only in the agent's base currency; multi-currency display with an agent-set spread is a fast-follow.
18. **PCI scope.** The design assumes **SAQ-A** — all card entry on Paystack's hosted page or iframe, no card data touching our servers. A fully in-page custom card form jumps us to SAQ-A-EP and a much heavier compliance burden. Confirm **before** checkout UI design.
19. **Branded email from custom domains.** Sending "from" an agent's own domain needs per-domain SPF/DKIM/DMARC. **Recommendation:** MVP sends from our domain with the agent's display name and reply-to; per-domain DKIM as a fast-follow. Otherwise the first hundred agents collectively destroy our sending reputation.
20. **Subdomain squatting.** Nothing stops an agent claiming `emirates.tripsagent.com`. Needs a reserved-word denylist plus manual review against a known-brand list — and someone must supply that list.
21. **Do storefront travellers get accounts?** FRD §2.1 mentions a "Customer storefront account" but §2.4 RS-2 has them checking out as a guest, and no use case creates an account. **Recommendation:** guest checkout only, with a magic-link "manage my booking" page.
22. **Multi-city and round-trip confirmations return arrays.** Does one priced journey become one order line with N supplier confirmations, or N order lines? **Recommendation:** one order line per priced journey with N child confirmation rows — keeps the invoice, the refund unit and the reversal unit aligned with what the traveller actually bought. Confirm this matches how Trips Africa expects the issue call.
23. **Platform fee on installments.** When a group departure is paid in four installments, is the fee taken on the deposit, spread across installments, or taken on completion? The ledger can do any of them; the commercial answer is not in the FRD.
24. **Loyalty and reviews** appear in §1.2 scope with no use case, business rules or UI. Modelled in M3 with assumed-standard mechanics, pending a requirements pass.
25. **Merchant of record for VAT and invoicing** — the agent or Trips? Affects invoice content and tax reporting.
26. **NDPA 2023 compliance.** Data residency (no Azure/AWS region in Nigeria — nearest is South Africa), retention, and the erasure right, which conflicts directly with the 7-year retention we need on financial records and audit logs. **Recommendation:** erasure implemented as PII anonymisation while preserving financial and audit records. Needs Nigerian legal counsel, not an engineering decision.
27. **Supplier operational unknowns — ask Trips Africa in writing.** No published rate limits, no concurrency ceiling, no documented staging data set, no failure-injection sandbox, and no support escalation path or SLA. Until these exist the poller must stay conservative (costing latency) and we can only contract-test reversal paths against our own mocks.

---

## 8. Verification

**Per-milestone, running locally against Docker Compose** (Postgres, Redis, RabbitMQ, MinIO, Mailpit):

- **Unit** — pricing resolution precedence, capacity arithmetic, ledger balancing, hash computation against the supplier's published sample vectors.
- **Integration (Testcontainers, real Postgres)** — tenant isolation (an agency cannot read another's rows even with filters bypassed, proven against RLS); wallet concurrency under parallel debits; departure capacity under parallel reservations; outbox at-least-once delivery with inbox dedup.
- **Supplier contract tests** — WireMock recordings of every documented Trips Africa response, including domestic array responses, `TicketPending`, status 100, and each reversal-triggering status. Plus a nightly smoke test against real staging.
- **Saga tests** — force failure at each state and assert the compensating action: payment captured + issue fails → line flagged, customer notified, reversal queued; hash mismatch → issuance blocked, alert raised; concurrent submits → exactly one `supplier_bookings` row.
- **E2E (Playwright)** — M1: signup → KYB → approve → top up → search → book → ticketed → invoice. M2: build site → connect domain → publish → customer books a group departure with deposit → lead in CRM. M3: admin creates a tier → agent subscribes → entitlement enforced → sub-agent blocked at allowance → cross-tenant report exports and is audited.
- **Load** — search endpoint at expected peak with cache warm and cold; ledger integrity assertion after a randomised transaction soak.
- **Manual** — real custom domain with real DNS and a real certificate; a real Paystack test payment through to settlement; a real ticket issued on Trips staging with a genuine PNR.
