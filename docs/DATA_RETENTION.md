# Data retention

How long every table keeps its rows, why, and what enforces it. Issue
[#105](https://github.com/innovateavitech/trips-agent/issues/105), part of the security epic
([docs/epics/0071-security-hardening.md](epics/0071-security-hardening.md)).

> **Status: draft, awaiting review.** The acceptance criterion asks for this schedule to be reviewed
> by whoever answers **open question 26** in the
> [delivery plan](ARCHITECTURE_AND_DELIVERY_PLAN.md) — NDPA 2023 erasure against the seven-year
> retention of financial and audit records. That answer needs Nigerian legal counsel, and it has not
> been given. Until it has, every period below is an engineering proposal, and the purge job runs in
> **dry-run mode**: it records what it would delete and deletes nothing.

---

## The rules this schedule follows

1. **Financial records, the audit log, and everything that supports them are kept at least seven
   years, and the purge job never touches them.** "Supports" means anything you would need to
   explain a number on an invoice or a wallet statement: the order, its frozen prices, the quote
   and markup rule they came from, the supplier booking behind them, the gateway's evidence.
2. **Unknown means keep.** When the job cannot tell whether a row is past its window — a trip
   whose dates we never received — it keeps the row.
3. **Operational data goes when it stops being useful.** Login attempts, expired tokens, delivered
   messages: kept long enough to investigate something, then deleted.
4. **Personal data inside a record we must keep is cleared, not the record.** A traveller's
   passport number goes 90 days after the trip; their name stays on the order.
5. **Every table is classified.** A test compares this schedule with the migrated database and
   fails when a table is missing from it — so a table added next year cannot quietly go
   unclassified. Another test fails if this document stops naming a table the code knows about.

---

## The schedule

**Treatments:**

- **Protected**: never touched by the purge job.
- **Purged**: rows deleted by the purge job.
- **Anonymised**: personal columns cleared, row kept.
- **Partitions dropped**: whole months dropped by a maintenance job.
- **Kept**: kept while what the row describes exists.
- **Not yet enforced**: a period is proposed, but nothing enforces it yet.

The source of truth is `RetentionCatalogue.Tables` in
`backend/services/TripsAgent.Infrastructure/Retention/RetentionCatalogue.cs`; this table mirrors it.

### Money

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `payments.ledger_accounts` | No | At least 7 years | Protected | The ledger's accounts. Every balance is rebuilt from these and their entries |
| `payments.ledger_entries` | No | At least 7 years | Protected | The ledger itself — the source of truth for every naira. Append-only in the database too |
| `payments.wallets` | No | At least 7 years | Protected | Balances the nightly audit reconciles against the ledger |
| `payments.wallet_holds` | No | At least 7 years | Protected | Funds reserved for a booking; each explains a statement line |
| `payments.wallet_transactions` | No | At least 7 years | Protected | The agent's wallet statement |
| `payments.payment_transactions` | Some (gateway references) | At least 7 years | Protected | The evidence behind every top-up and order payment |
| `payments.payment_webhook_events` | Yes (payer details in payloads) | At least 7 years | Protected | What the gateway told us, with its signature check — the evidence in a dispute |
| `payments.reconciliation_exceptions` | No | At least 7 years | Protected | What the ledger audit found and how it was resolved |

### Sales

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `orders.orders` | Yes (billing details) | At least 7 years | Protected | The sale: a financial and tax record |
| `orders.order_lines` | No | At least 7 years | Protected | Net, markup and tax frozen at purchase; every revenue report reads these |
| `orders.order_status_history` | No | At least 7 years | Protected | How each order moved and who moved it. Append-only in the database too |
| `orders.order_travellers` | Yes | Row: 7 years. Passport number and expiry: cleared 90 days after the trip | Anonymised | Who travelled belongs to the sale; their passport number does not, once the trip is over |
| `orders.carts` | Some (guest session token) | 30 days after expiry, if never converted | Purged | A cart that became nothing records nothing. Converted carts stay with their order |
| `orders.cart_items` | No | With their cart | Purged | Deleted by the cart's `ON DELETE CASCADE` |

### Documents and pricing

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `documents.generated_documents` | Yes (names on invoices) | At least 7 years | Protected | Issued invoices and vouchers; tax law requires them kept |
| `documents.document_number_sequences` | No | At least 7 years | Protected | Gapless numbering — a gap is a question from the tax authority |
| `documents.document_number_formats` | No | At least 7 years | Protected | How each issued number was formatted at the time |
| `pricing.price_quotes` | No | At least 7 years | Protected | The priced snapshot each order line was placed from |
| `pricing.markup_rules` | No | At least 7 years | Protected | Explains the markup on every historic order line |

### The agency's catalog

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `catalog.products` | No | While the agency exists; archived, never deleted | Kept | What the agency sells. Order lines freeze their own title and price, so this is not a financial record |
| `catalog.product_media` | No | With their product | Kept | Which uploads a product shows; detaching one never deletes the asset |
| `catalog.product_categories` | No | While in use | Kept | Configuration: the agency's own categories and themes |
| `catalog.product_category_map` | No | With their product | Kept | Which categories and themes a product carries |
| `catalog.tour_itinerary_days` | No | With their product | Kept | The itinerary; replaced whenever the product is saved |
| `catalog.product_inclusions` | No | With their product | Kept | What the price includes and leaves out; replaced whenever the product is saved |
| `catalog.product_price_variants` | No | With their product | Kept | Current prices only; a quote or order line froze its own copy |
| `catalog.visa_details` | No | With their product | Kept | What a visa product says about the visa |
| `catalog.visa_document_requirements` | No | With their product | Kept | The applicant's checklist: a list of documents, not anyone's documents |

### Supplier bookings

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `supplier.supplier_bookings` | No | At least 7 years | Protected | The airline or operator booking — the cost side of the order line |
| `supplier.supplier_booking_confirmations` | No | At least 7 years | Protected | Price confirmations and their hash checks |
| `supplier.supplier_booking_passengers` | Yes | At least 7 years | Protected | Who was ticketed; ticket numbers tie the supplier's invoice to ours. Erasing a person is [#106](https://github.com/innovateavitech/trips-agent/issues/106) |
| `supplier.supplier_status_polls` | No | At least 7 years | Protected | The evidence trail behind any payment reversal |
| `supplier.passenger_documents` | Yes (passport, visa) | 90 days after the trip ends; kept while the trip date is unknown | Purged | Needed to ticket and to fly, not to account for the sale afterwards |
| `supplier.supplier_api_calls` | Yes (in payloads) | 3 whole months — never less than 90 days | Partitions dropped | Full supplier requests and responses: large, and quickly worthless. See below |

### Supplier search

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `supplier.search_requests` | Some (who searched) | Proposed: 30 days | Not yet enforced | For the conversion report. Waits for the search work ([#33](https://github.com/innovateavitech/trips-agent/issues/33), [#40](https://github.com/innovateavitech/trips-agent/issues/40)), which owns these tables and their volume |
| `supplier.search_sessions` | No | Proposed: 30 days | Not yet enforced | A supplier session is dead within the hour |
| `supplier.supplier_offers` | No | Proposed: 30 days unless booked; a booked offer stays with its booking | Not yet enforced | A booked offer gives the trip dates the travel-document rule depends on |
| `supplier.flight_segments` | No | With their offer | Not yet enforced | The trip dates |
| `supplier.bus_segments` | No | With their offer | Not yet enforced | The trip dates |
| `supplier.supplier_fare_rules` | No | With their offer or booking | Not yet enforced | The fare rules the traveller was shown |
| `supplier.suppliers` | No | While the supplier is used | Kept | Configuration |
| `supplier.supplier_credentials` | No | While in use; rotated, not accumulated | Kept | Encrypted merchant keys |

### Identity

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `identity.users` | Yes | While the account exists | Kept | Account holders. Erasing a person is [#106](https://github.com/innovateavitech/trips-agent/issues/106), blocked on open question 26 |
| `identity.roles` | No | While in use | Kept | Configuration |
| `identity.permissions` | No | While in use | Kept | Reference data |
| `identity.role_permissions` | No | While in use | Kept | Configuration |
| `identity.user_roles` | No | While the account exists | Kept | Who may do what |
| `identity.login_attempts` | Yes (email, IP, browser) | 90 days | Purged | Feeds lockout and investigations; no use after a quarter |
| `identity.refresh_tokens` | Some (creation IP) | 30 days after expiry | Purged | Hashes only. Kept past expiry long enough to investigate token reuse |
| `identity.password_reset_tokens` | No | 30 days after expiry | Purged | A dead link has no use once any question about it is answered |
| `identity.otp_codes` | Yes (the address it was sent to) | 30 days after expiry | Purged | As above |
| `identity.user_invitations` | Yes (invitee's email) | 90 days after expiry, if never accepted | Purged | Holds the email of someone who never joined. Accepted invitations are kept |

### Agencies

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `tenancy.agencies` | Some (business contacts) | At least 7 years | Protected | The customer; every financial record hangs off it |
| `tenancy.agency_settings` | No | While the agency exists | Kept | Configuration |
| `tenancy.agency_branding` | Some (contact address) | While the agency exists | Kept | Configuration |
| `tenancy.kyb_submissions` | Yes (directors) | At least 7 years | Protected | Proof the agency was verified before it could transact |
| `tenancy.kyb_documents` | Yes | At least 7 years | Protected | The documents that verification rested on |

### Platform

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `platform.audit_logs` | Yes (actors, IPs, redacted before/after state) | 84 months, then whole monthly partitions dropped by `audit-log-maintenance` | Protected | Who did what, when. This job never touches it — see [runbooks/audit-log.md](runbooks/audit-log.md) |
| `platform.admin_alerts` | No | Indefinitely, for now | Kept | Small, and a resolved alert records how an incident was handled. Revisit if it grows |
| `platform.outbox_messages` | Some (event payloads) | 30 days after dispatch; failed messages kept | Purged | Delivered events. A failed one waits for a person |
| `platform.inbox_messages` | No | 30 days after processing — never under 7 | Purged | Deduplication, only useful while a broker might redeliver |
| `platform.assets` | Yes (uploaded files) | While referenced | Kept | Logos, KYB documents. Abandoned uploads are expired by `asset-pipeline-sweep` |
| `platform.asset_variants` | No | With their asset | Kept | Resized copies |

### Notifications

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `notifications.notifications` | Yes (recipient, content) | 365 days, once no longer queued or sending | Purged | Answers "did they get it?" for a year |
| `notifications.notification_templates` | No | While in use | Kept | Configuration |
| `notifications.suppressed_email_addresses` | Yes | Indefinitely | Kept | A bounced or complaining address must stay suppressed, or we mail it again |

### Storefront

| Table | Personal data | Kept for | Treatment | Why |
|---|---|---|---|---|
| `storefront.site_templates` | No | While offered | Kept | Reference data: the starter websites |
| `storefront.reserved_hostname_labels` | No | Indefinitely | Kept | Reference data: the hostname denylist and brand list (open question 20) |
| `storefront.sites` | No | While the agency exists | Kept | The agency's website and its settings |
| `storefront.site_versions` | Some (agency contact details in snapshots) | While the site exists | Kept | Every staged and published version: rollback needs them, and they record what travellers were shown |
| `storefront.site_pages` | No | While the site exists | Kept | The draft's pages |
| `storefront.site_blocks` | No | With their page | Kept | The draft's blocks |
| `storefront.site_themes` | No | While the site exists | Kept | The site's typography |
| `storefront.site_domains` | No | While connected | Kept | Hostnames the site answers on; a removed one is deleted with its checks |
| `storefront.site_domain_checks` | No | With their hostname | Kept | What DNS said on each check — the answer to "why isn't my domain working?" |

---

## The purge job

`data-retention`, a Hangfire recurring job in `TripsAgent.Worker`, daily at **03:10 UTC**
(`DataRetentionSchedule`). Runbook: [runbooks/data-retention.md](runbooks/data-retention.md).

**Dry run by default.** `DataRetention__DryRun` is `true` unless set otherwise. A dry run counts
exactly the rows a live run would delete — the same SQL condition, used once in a `SELECT count(*)`
and once in the `DELETE` — and records the count. Deleting cannot be undone and the job runs
unattended; the dry run is how a wrong `WHERE` clause is found before it costs someone their
booking history.

**Every run audits every table.** One row per table per run in `platform.audit_logs`:
`entity_type = 'DataRetention'`, `entity_id` = the table, `action` one of `retention.dry_run`,
`retention.deleted`, `retention.anonymised`, `retention.partitions_dropped` or `retention.failed`,
and the table, row count, window and cutoff in `after_state`. In a live run the deletion and its
audit row commit in one transaction, so nothing is deleted without its record.

```sql
-- What did the last few runs do, or propose to do?
SELECT occurred_at, action, entity_id AS "table", after_state->>'rows' AS rows,
       after_state->>'window' AS "window", after_state->>'cutoff' AS cutoff
  FROM platform.audit_logs
 WHERE entity_type = 'DataRetention'
 ORDER BY occurred_at DESC
 LIMIT 100;
```

**Idempotent.** The cutoff is the start of today (UTC) less the window, so every run on the same
day draws the same line, and a second run finds nothing the first did not already handle. The
anonymise rule only matches rows that still hold something to clear.

**What stops it touching a financial record.** Five things, any one of which is enough:

1. The job only runs rules whose table the catalogue classifies as *Purged* or *Anonymised*: an
   allowlist. A new table nobody has classified cannot be targeted.
2. It refuses outright — before running anything — if any rule names a *Protected* table: a
   denylist, checked again beside each statement.
3. It connects as `tripsagent_app`, which has no `DELETE` at all on the ledger, the audit log,
   orders, order lines, their history or issued documents.
4. The ledger, the audit log, order history and issued documents also have triggers that refuse
   `DELETE` from any role, the schema owner included.
5. Tests: a unit test tries every protected table and expects a refusal, and an integration test
   runs the job for real with every window at one day over a database holding old rows in every
   protected table, and checks that not one of them changes.

**The supplier call log** is plan job 30, and it already exists as `supplier-api-call-maintenance`
(03:15 UTC, `SupplierApiCalls__RetentionMonths`, default 3). It drops whole monthly partitions,
which is how 90-day hot retention is enforced without deleting row by row. This job does not
duplicate it: each run reports how many rows are past the window, and a live run calls the same
maintenance. That job predates this one and never had a dry run, so it keeps dropping expired
partitions on its own schedule whatever `DataRetention__DryRun` says.

**Travel documents** are counted from the end of the trip: the last flight arrival, or the last bus
arrival (its departure when the operator gives none), on the offer the booking was made from.
`supplier.passenger_documents` rows are deleted; in `orders.order_travellers` the passport number
and expiry are cleared and the traveller's name stays with the order. A booking whose trip dates we
do not have is kept until we do.

### Settings

Windows are whole days (`.env.example` lists them all):

| Setting | Default | Applies to |
|---|---|---|
| `DataRetention__DryRun` | `true` | Everything below |
| `DataRetention__LoginAttemptDays` | 90 | `identity.login_attempts` |
| `DataRetention__ExpiredCredentialDays` | 30 | Refresh tokens, reset tokens, verification codes, after expiry |
| `DataRetention__ExpiredInvitationDays` | 90 | Unaccepted invitations, after expiry |
| `DataRetention__ProcessedMessageDays` | 30 (minimum 7) | Outbox after dispatch, inbox after processing |
| `DataRetention__NotificationDays` | 365 | Notifications |
| `DataRetention__ExpiredCartDays` | 30 | Unconverted carts, after expiry |
| `DataRetention__TravelDocumentDays` | 90 | Passport and visa details, after the trip |

A window under one day stops the Worker starting, rather than failing quietly at 03:10.

---

## Not covered yet

Honest gaps, so nobody assumes they are handled:

- **Deleting financial records after seven years.** Nothing does, and nothing should until open
  question 26 is answered. "At least 7 years" above means exactly that.
- **Erasure of a person on request** (NDPA). That is [#106](https://github.com/innovateavitech/trips-agent/issues/106),
  blocked on the same question. This job only clears travel documents after the trip.
- **Supplier search tables.** Proposed periods are above; enforcement lands with the search work,
  which owns those tables.
- **Outside the database.** Application logs (the rate limiter logs the address or user it refuses),
  database backups, Redis and blob storage all hold personal data too. Their retention is set by
  the hosting platform, and the cloud has not been chosen yet. Rate-limit counters in Redis expire
  with their own window — minutes, at most an hour.

## Changing this schedule

1. Change `RetentionCatalogue` (the classification, and a rule if the job should enforce it).
2. Change the matching row in this document — a test fails if a table is missing from it.
3. Add or change the setting in `DataRetentionOptions`, `appsettings.json` and `.env.example`.
4. Run it dry first, read the audit rows, then switch it on. See the runbook.
