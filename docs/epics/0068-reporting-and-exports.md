# Epic #68 — Reporting and exports

**Epic:** [#68](https://github.com/innovateavitech/trips-agent/issues/68) ·
**Module:** Analytics & Reporting · **Milestone:** M3 — Network, monetisation & back-office
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Sync for small scopes; ASYNC when over 90 days or cross-tenant, notifying on completion;
> CSV/XLSX export; scheduled recurring reports emailed to a distribution list; drill-down from
> aggregate to transaction; EVERY export logged with actor, scope, row count and timestamp — the
> FRD requires this explicitly given cross-tenant sensitivity.

Twelve issues below cover all six of those. The split is driven by one structural decision made
early and then relied on everywhere: **a report is a declared object, not a query a caller
supplies.** R2 establishes that, and R3–R12 all sit behind it.

---

## Read this before picking anything up

**None of these can start today.** This is M3 work sitting at the far end of the dependency
chain. It rests on [#67](https://github.com/innovateavitech/trips-agent/issues/67) (the read
models this epic reads *from*), which rests on
[#41](https://github.com/innovateavitech/trips-agent/issues/41) (orders),
[#31](https://github.com/innovateavitech/trips-agent/issues/31) (messaging) and
[#8](https://github.com/innovateavitech/trips-agent/issues/8) (EF Core) — none of which are
closed.

Breaking it down now is still worth doing, because three of the findings below change decisions
made earlier than M3. The export-audit requirement wants a writer that
[#21](https://github.com/innovateavitech/trips-agent/issues/21) is the natural home for; the
drill-down in R10 is the cheapest existing test that
[#67](https://github.com/innovateavitech/trips-agent/issues/67)'s rollups are actually correct;
and nothing currently owns the XLSX library choice.

**Everything here reads from the `analytics` read models, never from `orders`.** Plan §2.13 is
explicit that dashboards are never queried live off the OLTP tables, and that constraint is what
makes the sync/async split tractable at all. A report that reaches into `orders` because it was
easier defeats the entire epic.

---

## The split

`R1`–`R12` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| R1 | Reporting schema — definitions, jobs, schedules, export audit | ~1 day | #8, #10, #11, #67 |
| R2 | Report definition registry and parameter validation | ~2 days | R1 |
| R3 | Report execution over the read models | ~2 days | R2 |
| R4 | Sync vs async routing and the threshold rule | ~2 days | R3, #31 |
| R5 | CSV export writer | ~1 day | R3 |
| R6 | XLSX export writer | ~2 days | R3 |
| R7 | Export audit on every path ⚠️ | ~1 day | R4, R5, R6, #21 |
| R8 | `ReportGenerationWorker` | ~2 days | R7, #18, #45 |
| R9 | Job status and secure result download ⚠️ | ~2 days | R8 |
| R10 | Drill-down from aggregate to transaction | ~2 days | R3 |
| R11 | `ScheduledReportDispatcher` and distribution lists | ~2 days | R8 |
| R12 | Reports UI — agent console and admin console | ~3 days | R4, R9, #48, #66 |

All twelve carry `module:analytics-reporting` and the `M3` milestone. R7 and R9 touch tenancy and
should be reviewed with the care CLAUDE.md rule 3 asks for.

**Order:** R1 → R2 → R3 first, because everything else is behind them. Then R5 and R6 in parallel
with R4. R7 must land before R8, not after — an audit added to a working export is an audit with
a hole in it for however long it takes. R10 can run any time after R3 and is worth doing early,
for the reason in finding 2. R12 last.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### R1 · `Analytics & Reporting: Reporting schema`

#### What

The four tables from plan §2.13: `report_definitions`, `report_jobs`, `report_schedules`,
`report_exports_audit`. EF configuration and a migration.

#### Notes

`report_jobs.scope` is `agency|platform`, with `status`, `result_asset_id` and `format`.
Tenant-scoped rows carry `agency_id` and the global query filter; platform-scope rows carry
`null` and are reachable only through the audited path in R9.

`report_exports_audit` is **append-only**. It is the record that proves who read what, so it must
not be editable by the code that writes it — the same reasoning as the price-freeze trigger in
CLAUDE.md rule 5.

#### Acceptance criteria

- [ ] Four tables created with a migration that runs clean on an empty database
- [ ] Tenant-scoped tables have `agency_id` and a global query filter (CLAUDE.md rule 3)
- [ ] `report_exports_audit` has no update or delete path from application code
- [ ] Architecture test: nothing in `Domain` references these — they are infrastructure read models
- [ ] Index on `report_jobs (agency_id, status, created_at)` for the job list in R9

**Depends on:** #8, #10, #11, #67

---

### R2 · `Analytics & Reporting: Report definition registry and parameter validation`

#### What

A report definition is a declared object: id, title, source read model, selectable columns,
filterable fields, maximum date range, allowed scope (`agency`, `platform`, or both), and the
entitlement required to run it. Callers pick a definition and supply parameters; they never
supply columns, tables or predicates.

#### Why it is its own issue

This is the boundary that stops "reporting" from quietly becoming "arbitrary query endpoint over
every tenant's data". Landing it *before* the execution layer means the unsafe version never
exists, not even briefly on a branch. Retrofitting it later means auditing every caller.

#### Acceptance criteria

- [ ] Definitions are registered in code and enumerable — a caller can list what it may run
- [ ] Parameters validated against the definition before execution: unknown field rejected,
      out-of-range date rejected, scope the caller lacks rejected
- [ ] Requesting a `platform`-scope definition without the entitlement fails closed, and the
      failure is indistinguishable from "no such report" (do not confirm it exists)
- [ ] Validation is a unit test, not an integration test — no database needed
- [ ] Adding a definition requires no change to R3, R5, R6 or R12

**Depends on:** R1

---

### R3 · `Analytics & Reporting: Report execution over the read models`

#### What

Turns a validated definition plus parameters into a parameterised query against `fact_bookings`
and the `agg_*` tables, and returns a streaming reader. Dapper, per plan §1 — EF's change tracker
is dead weight for a read this size.

#### Acceptance criteria

- [ ] Reads only from `analytics` tables — an architecture test fails the build if a report type
      references `orders`, `order_lines` or any OLTP table
- [ ] Tenant predicate applied from `ITenantContext`, never from a caller-supplied `agency_id`
- [ ] Hard row cap; exceeding it is an error telling the caller to narrow the range, never a
      silent truncation
- [ ] Money stays `long` minor units end to end — no formatting in this layer (CLAUDE.md rule 2)
- [ ] Streams rather than materialises: a million-row result must not be held in memory
- [ ] Integration test with two agencies proves agency B's rows never appear in agency A's report

**Depends on:** R2

---

### R4 · `Analytics & Reporting: Sync vs async routing and the threshold rule`

#### What

The FRD's rule for UC-1C. Small scopes run inline and return results. Anything over 90 days or
crossing tenants is enqueued to `reports.generate` and returns `202` with a `report_jobs` id.

#### Notes

`reports.generate` gets its own consumer pool (plan §2 names it as an isolated, long-running
queue). A twelve-month cross-tenant export must not be able to starve `booking.saga`, which is
the highest-priority queue and the one with real money behind it.

See finding 1 — the threshold as written is probably not sufficient on its own.

#### Acceptance criteria

- [ ] Over 90 days → async. Cross-tenant → async, regardless of range
- [ ] Under both → sync, with a request timeout that is shorter than the gateway's
- [ ] Async returns `202` and a job id immediately; it never blocks waiting for the worker
- [ ] `reports.generate` is bound to its own pool, verified by a test that saturates it and
      asserts `booking.saga` still consumes
- [ ] The routing decision is a pure function, unit-tested at the boundaries: 89, 90 and 91 days

**Depends on:** R3, #31

---

### R5 · `Analytics & Reporting: CSV export writer`

#### What

Streaming CSV from an `IReportResult`. RFC 4180 quoting and escaping.

#### Acceptance criteria

- [ ] Streams — memory stays flat across a 500k-row export
- [ ] UTF-8 **with BOM**, so Excel opens Nigerian names and `₦` correctly instead of as mojibake
- [ ] Money rendered from minor units at this boundary only (`150000` → `1500.00`)
- [ ] Fields containing comma, quote or newline are quoted and escaped; round-trip test proves it
- [ ] **Formula injection guarded:** a value starting `=`, `+`, `-` or `@` is prefixed so a
      spreadsheet treats it as text. A customer name is attacker-controlled text arriving in a
      file someone opens on their laptop
- [ ] Header row uses the definition's column titles, not database column names

**Depends on:** R3

---

### R6 · `Analytics & Reporting: XLSX export writer`

#### What

The same interface as R5, producing XLSX. See finding 3 — the library is not chosen yet.

#### Acceptance criteria

- [ ] Streams to disk or a temp file; does not build the whole workbook in memory
- [ ] Money written as **typed numeric cells with a currency format**, not strings — the recipient
      must be able to sum a column without cleaning it first
- [ ] Dates written as date cells, not text
- [ ] Header row frozen and an auto-filter applied — this is the format finance staff live in
- [ ] Opens without a repair prompt in Excel, LibreOffice and Google Sheets
- [ ] A 250k-row export completes within the worker's timeout

**Depends on:** R3

---

### R7 · `Analytics & Reporting: Export audit on every path` ⚠️

#### What

Every export writes `report_exports_audit`: actor, scope, definition, filters, row count, format,
timestamp. The FRD requires this explicitly (§2.15 UC-1C RS-6) because these exports can cross
tenants.

#### Why the placement matters more than the writing

Writing the audit row is half a day. The requirement is that **no path can produce rows without
one**, and that is a design constraint, not a line of code. It belongs inside the execution layer
where results are produced, not in a controller — because there are four ways out (sync response,
async worker, scheduled run, and re-download of a stored result) and a controller-level audit
covers one of them.

#### Acceptance criteria

- [ ] Audit row written in the same transaction that records the result, not best-effort after
- [ ] **Four separate tests**, one per path: sync, async worker, scheduled run, re-download
- [ ] Row count recorded is the actual count delivered, not the requested cap
- [ ] Failed and cancelled exports are recorded too, with the row count they reached
- [ ] Attempting to export with the audit writer unavailable **fails the export** — the audit is
      not optional, and a report is less costly to lose than an unlogged cross-tenant read
- [ ] Test proves there is no public path from an `IReportResult` to bytes that skips the audit

**Depends on:** R4, R5, R6, #21

---

### R8 · `Analytics & Reporting: ReportGenerationWorker`

#### What

Plan job #24. Consumes `reports.generate`, runs the query, writes the file through `IBlobStorage`
via the asset pipeline, updates `report_jobs`, and notifies the requester on completion.

#### Acceptance criteria

- [ ] Consumes from the isolated `reports.generate` pool
- [ ] Result stored via `IBlobStorage` — no cloud SDK referenced directly (CLAUDE.md, stack)
- [ ] `report_jobs` moves `queued → running → succeeded|failed` with a timestamp on each
- [ ] Failure records a reason the requesting user can read and act on, not a stack trace
- [ ] Notification on completion goes through the dispatcher in #45
- [ ] A job that dies mid-run is retried or marked failed by a reaper — it never sits in `running`
      forever, which is the state users report as "my export is stuck"
- [ ] Retry is safe: a re-run overwrites its own result rather than appending

**Depends on:** R7, #18, #45

---

### R9 · `Analytics & Reporting: Job status and secure result download` ⚠️

#### What

An endpoint to poll job status and one to download the result via a short-lived signed URL.

#### Why this is the sensitive one

The stored result is a file containing exactly the cross-tenant data the audit requirement exists
for, and it outlives the request that produced it. A twelve-month platform report can finish
after the person who asked for it has changed role or left.

#### Acceptance criteria

- [ ] **Scope re-checked at download time against the actor's current permissions**, not only at
      request time
- [ ] Download URL is short-lived and single-purpose; a leaked URL expires quickly
- [ ] A user cannot fetch another agency's job by guessing its id — tested, not assumed
- [ ] Every download writes its own `report_exports_audit` row (R7)
- [ ] Job list is filtered to the caller's own jobs by the tenant filter
- [ ] If the platform-admin path needs `.IgnoreQueryFilters()`, it lives in **one** reviewed,
      audited place — CLAUDE.md rule 3 permits this for platform-admin reporting, and this is
      that case

**Depends on:** R8

---

### R10 · `Analytics & Reporting: Drill-down from aggregate to transaction`

#### What

Clicking a figure in an aggregate opens the `fact_bookings` rows behind it, carrying the same
filters and the same tenant predicate.

#### Notes

This is also the cheapest correctness test the analytics work has. See finding 2.

#### Acceptance criteria

- [ ] Every aggregate cell can produce the filtered transaction list behind it
- [ ] **The drill-down rows sum to the aggregate cell.** A mismatch fails the test, and means the
      rollup in #67 is wrong
- [ ] Same tenant predicate as the aggregate — drill-down is not a way around the filter
- [ ] Paginated; a drill-down on a large cell must not attempt to return everything
- [ ] Drill-down results are exportable, and that export is audited like any other (R7)

**Depends on:** R3

---

### R11 · `Analytics & Reporting: ScheduledReportDispatcher and distribution lists`

#### What

Plan job #25. A Hangfire cron per `report_schedules` row that enqueues a job and delivers the
result to a distribution list.

#### Notes

See finding 4 — whether the delivery is an attachment or a link is not settled, and it changes
this issue's shape.

#### Acceptance criteria

- [ ] Schedules stored with an explicit timezone, defaulting to `Africa/Lagos` — "every Monday
      08:00" is meaningless without one, and the platform's users are in one place
- [ ] A run that is still going when the next fires **does not start a second run**
- [ ] Runs missed during downtime are handled by an explicit, documented rule rather than by
      whatever Hangfire happens to do
- [ ] Recipients are stored per schedule; removing a recipient stops delivery immediately
- [ ] Delivery emails are branded to the **agent**, not to Trips, for agency-scope schedules
      (CLAUDE.md rule 4)
- [ ] Disabling a schedule stops it without deleting its history

**Depends on:** R8

---

### R12 · `Analytics & Reporting: Reports UI`

#### What

*agent-console*: report picker, parameter form, inline results for small scopes, an explicit
"we will email you when this is ready" state for async, job history, and schedule management.
*admin-console*: platform-scope reports plus a read-only `report_exports_audit` viewer.

#### Acceptance criteria

- [ ] `packages/ui` components and design tokens only — `pnpm check:design` passes (CLAUDE.md
      rule 7)
- [ ] The async hand-off is explicit. A spinner that silently becomes an email is the single
      most confusing thing this feature can do
- [ ] Drill-down (R10) is reachable from aggregate figures
- [ ] Export buttons state the format and that the export will be logged
- [ ] Money formatted from minor units in one shared helper, not per screen
- [ ] Empty, loading, failed and "too large, narrow your range" states are all designed

**Depends on:** R4, R9, #48, #66

---

## What this breakdown found

Four things worth acting on, two of them before M1 finishes.

### 1. The 90-day threshold does not actually describe the problem

The epic and the plan both route on "over 90 days or cross-tenant". But range is a poor proxy for
cost: a 30-day report for a high-volume agency can return far more rows than a 120-day report for
a small one, and it is **row count** that decides whether a request finishes inside an HTTP
timeout.

Taken literally, the rule sends a large 30-day report down the sync path, where it times out —
and the user's only recourse is to ask for *more* data to trigger the async path.

**Suggested:** route async on any of — over 90 days, **or** an estimated row count over a
threshold, **or** cross-tenant. It keeps the FRD's rule intact and adds the condition that
actually governs latency. Flagged rather than decided, per CLAUDE.md.

### 2. R10 is the cheapest correctness test #67 will get

[#67](https://github.com/innovateavitech/trips-agent/issues/67) requires that rebuilding the
rollups reproduces identical numbers. That is checked by rebuilding and comparing — which catches
a rollup that is *consistently* wrong only if the bug is in the rebuild path.

R10's assertion is different and stronger: the transactions behind a figure must sum to that
figure. It compares the aggregate against the underlying facts rather than against another run of
itself, so it catches a rollup that has been quietly wrong since the day it was written.

**Suggested:** build R10 early rather than last, and mention the assertion on #67 now while it is
still unstarted.

### 3. Nothing owns the XLSX library choice

CSV needs no dependency. XLSX does, and it is a real decision — streaming support, licence, and
whether it is maintained. The plan names QuestPDF for documents but nothing for spreadsheets, and
R6 would end up picking one by default.

Worth deciding deliberately, and worth an ADR if the answer is a commercial licence — this is the
second document-generation dependency in the stack, and someone will ask why they are different.

### 4. Scheduled reports put financial and personal data in mailboxes nobody controls

R11 emails recurring reports to a distribution list. If that is an attachment, a spreadsheet of
traveller names and revenue is now in an unknown number of inboxes, forwardable, outside every
control this epic builds — while R7 exists specifically so that every export is attributable.

The two requirements are in tension, and the FRD does not resolve it.

**Suggested:** deliver a signed authenticated link rather than an attachment, so the download is
audited like every other export and access can be revoked. Slightly worse to use; consistent with
the rest of the epic. This needs an answer before R11 is picked up.

### 5. Open questions this epic touches

Per [CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), these are flagged rather than
guessed at:

| # | Question | Effect here |
|---|---|---|
| 4 | Is the platform fee added to the traveller's price or taken from the agent's margin? | Decides what "margin" means in every revenue report. Changing it later re-derives every historical figure |
| 7 | Hierarchy depth — is principal → sub-agent enough? | `fact_bookings.root_agency_id` rolls a network up in one scan at depth 2. Depth 3+ changes the rollup and every network report |
| 25 | Merchant of record for VAT and invoicing | Decides whether tax appears as a platform or agent figure in financial reports |

Additionally, and not currently in §7: **does a principal's report include sub-agent data, and at
what margin visibility?** The plan says a sub-agent cannot see the principal's margin; the reverse
direction is not stated anywhere. It needs an answer before R3 fixes the tenant predicate.

### 6. Report result retention is unspecified

`report_jobs.result_asset_id` points at a file of cross-tenant financial data with no stated
expiry. Left alone it accumulates indefinitely, and every one of those files is a copy of data the
audit trail was built to protect.

**Suggested:** a short retention window with a documented purge job. Small enough to fold into R8,
but only once someone says what the window is.

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#68](https://github.com/innovateavitech/trips-agent/issues/68)
3. Create R1–R12 with `module:analytics-reporting` + `M3`, rewriting the `Depends on:` lines with
   real numbers
4. Take findings 1 and 4 to the client — both change a child issue's shape, and both are cheaper
   to answer now than to unpick in M3
5. Comment on [#67](https://github.com/innovateavitech/trips-agent/issues/67) about the
   drill-down assertion in finding 2, while it is still unstarted
6. Raise the XLSX library choice against Platform Foundation
7. Regenerate the map: `./scripts/generate-backlog.sh`
