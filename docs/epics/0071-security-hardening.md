# Epic #71 — Security hardening and load testing

**Epic:** [#71](https://github.com/innovateavitech/trips-agent/issues/71) ·
**Module:** Security · **Milestone:** M3 — Network, monetisation & back-office
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Rate limiting per user, IP and endpoint; RLS enforcement test suite; PII encryption and
> retention policy; NDPA erasure as anonymisation preserving financial and audit records;
> penetration test remediation; load test of the search endpoint at expected peak, cold and warm
> cache.

Nine issues below cover all six of those, plus two adjacent pieces the plan implies for
pre-launch readiness (security headers and CI scanning) — both of which make the penetration test
considerably cheaper, because they remove the findings a scanner would have caught for free.

---

## Read this before picking anything up

**None of these can start today.** This is M3 work, and every foundation it rests on is still
open: [#7](https://github.com/innovateavitech/trips-agent/issues/7) (CI),
[#12](https://github.com/innovateavitech/trips-agent/issues/12) (RLS),
[#16](https://github.com/innovateavitech/trips-agent/issues/16) (JWT),
[#32](https://github.com/innovateavitech/trips-agent/issues/32) (supplier schema),
[#40](https://github.com/innovateavitech/trips-agent/issues/40) (search cache).

That is not a reason to delay the breakdown — knowing the shape of the security work changes
decisions made in M1. Two examples from this document: the rate limiter needs a Redis client that
**no issue currently owns** (see [Gaps](#what-this-breakdown-found)), and the RLS test suite is
much easier to write if [#12](https://github.com/innovateavitech/trips-agent/issues/12) leaves
behind a way to enumerate tenant-scoped tables.

Security work is also **not** only these nine issues. Hardening that belongs to a feature stays
with that feature: account lockout lives in
[#15](https://github.com/innovateavitech/trips-agent/issues/15), refresh-token rotation in
[#16](https://github.com/innovateavitech/trips-agent/issues/16), supplier credential encryption
in [#32](https://github.com/innovateavitech/trips-agent/issues/32), header redaction in
[#39](https://github.com/innovateavitech/trips-agent/issues/39), audit redaction in
[#21](https://github.com/innovateavitech/trips-agent/issues/21). This epic is what is left over:
the cross-cutting work, and the proof that the rest of it holds.

---

## The split

`S1`–`S9` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| S1 | Rate limiting per IP, user and agency | ~2 days | #16, Redis (see gaps) |
| S2 | RLS enforcement test suite | ~2 days | #12, #7 |
| S3 | PII encryption at rest for traveller documents | ~2 days | #32, #41 |
| S4 | Data retention policy and the purge job | ~2 days | #21, #31, #32 |
| S5 | NDPA erasure as anonymisation 🚫 | ~2 days | #21, #22, #62 — **blocked** |
| S6 | Security headers, CORS and cookie hardening | ~1 day | #16, #59, #60 |
| S7 | Dependency and secret scanning in CI | ~1 day | #7 |
| S8 | Load test the search endpoint, cold and warm | ~2 days | #33, #40 |
| S9 | Penetration test — scope, execution, remediation | ~2 days + vendor | S1–S8 |

All nine carry `module:security` and the `M3` milestone. S5 additionally carries `blocked` and
`needs-decision`.

**Order:** S7 first (it is cheap and starts paying immediately), then S2 and S1, then S3 → S4 →
S6, then S8, and S9 last — a penetration test run before the other eight is a test of a system
you already know is unfinished, and you pay for the report twice.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### S1 · `Security: Rate limiting per IP, user and agency`

#### What
ASP.NET Core rate limiting middleware, backed by Redis so a limit means the same thing however
many API instances are running.

#### Why three partition keys
An IP limit alone punishes a whole office behind one NAT. A user limit alone does nothing to an
attacker who has not logged in. And because one agency's traffic must never degrade another's,
`agency_id` is the third partition — the same isolation principle as the tenant filter, applied
to capacity instead of data.

#### Acceptance criteria
- [ ] Partitioned by client IP for anonymous requests, by user id once authenticated, and by
      `agency_id` above that
- [ ] Per-endpoint policies, tightest where a request costs money or leaks information: login,
      registration, OTP resend, forgot-password, and search
- [ ] **Redis-backed**, not in-memory — an in-memory limiter is per-process, so with two API
      instances the real limit is double what it says, and neither instance knows
- [ ] Returns `429` with `Retry-After`, plus the `RateLimit-*` response headers
- [ ] The Paystack webhook endpoint is exempt or has a far higher ceiling — throttling a gateway
      callback means dropping payment notifications, which costs real money
- [ ] Limits are configuration, not constants, and every new variable is added to `.env.example`
- [ ] Rejections are logged with the partition key, so abuse is visible rather than silent
- [ ] Integration test: N+1 requests inside the window get a `429`, and a second API instance
      sharing the same Redis agrees

#### Notes
The client IP behind a load balancer or Cloudflare is in `X-Forwarded-For`, not
`RemoteIpAddress`. Configure `ForwardedHeaders` with a **known proxy allowlist** — trusting the
header unconditionally lets anyone set their own IP and walk straight past the limiter.

**Depends on:** #16

---

### S2 · `Security: RLS enforcement test suite`

#### What
The test suite that proves the PostgreSQL Row-Level Security backstop from
[#12](https://github.com/innovateavitech/trips-agent/issues/12) actually holds — and that fails
the build when a new tenant-scoped table ships without a policy.

#### Why it is a separate issue from #12
#12 writes the policies. This writes the proof, and the proof is the part that keeps working
after everyone who remembers writing the policies has moved on. A backstop nobody tests is a
backstop nobody knows is broken.

#### Acceptance criteria
- [ ] Testcontainers suite connecting as the **application** role, never the migration or owner
      role
- [ ] A test that deliberately calls `.IgnoreQueryFilters()` and still cannot read another
      agency's rows — this is the actual proof, and it is the one test that must never be deleted
- [ ] A schema test that enumerates every table with an `agency_id` column and fails if any lacks
      `ROW LEVEL SECURITY` enabled **and** a policy — so a table added in six months cannot
      quietly miss it
- [ ] Write coverage, not only reads: inserting or updating a row carrying another agency's
      `agency_id` must fail
- [ ] Asserts the application role is neither `BYPASSRLS` nor the owner of the tables (a table
      owner bypasses RLS by default unless `FORCE ROW LEVEL SECURITY` is set)
- [ ] Runs in CI on every pull request, in the `test-api` job

**Depends on:** #12, #7

---

### S3 · `Security: PII encryption at rest for traveller documents`

#### What
Column-level encryption for the traveller PII the plan already marks as encrypted:
`passenger_documents.doc_number_encrypted` and `order_travellers.passport_number_encrypted`.

#### Acceptance criteria
- [ ] An `IFieldEncryptor` port with the key source behind it — **no cloud KMS SDK**, because the
      cloud is not chosen yet and this would quietly choose it
- [ ] AES-256-GCM, a fresh IV per value, and the key id stored alongside the ciphertext so keys
      can rotate without rewriting the table in one migration
- [ ] Applied through an EF Core value converter, so encryption is not something a developer has
      to remember at each call site — the ones they forget are the ones that leak
- [ ] Plaintext never reaches logs, `audit_logs` before/after state, or `supplier_api_calls`
      payload dumps
- [ ] A documented rotation path: re-encrypt on write under the new key id, retain old keys for
      decrypt only
- [ ] Searching or filtering on these columns is explicitly **not** supported, and the issue says
      why — deterministic encryption would make it possible and leak equality at the same time
- [ ] Test: a raw SQL `SELECT` against the column returns ciphertext, not a passport number

**Depends on:** #32, #41

---

### S4 · `Security: Data retention policy and the purge job`

#### What
A written retention schedule, and the scheduled job that enforces it.

#### Acceptance criteria
- [ ] A retention table in `docs/` — every table that holds personal or operational data, how
      long it is kept, and the reason. Reviewed by whoever answers open question 26
- [ ] Financial records, `audit_logs` and anything supporting them: **7 years, never touched by
      this job**
- [ ] `supplier_api_calls`: 90-day hot retention by dropping monthly partitions — this is job 30
      in the plan, so it lands here rather than being invented twice
- [ ] Traveller documents purged or anonymised a defined interval after travel completes
- [ ] Implemented as a Hangfire job with a **dry-run mode** that reports what it would delete
      without deleting it, and dry-run is the default for the first release
- [ ] Every run writes an audit row: table, row count, window
- [ ] Idempotent — running it twice in a day deletes nothing extra and errors nowhere

#### Notes
Deleting data is not reversible and this job runs unattended. The dry run is not ceremony; it is
how you find out you got a `WHERE` clause wrong before it costs you a customer's booking history.

**Depends on:** #21, #31, #32

---

### S5 · `Security: NDPA erasure as anonymisation` 🚫

#### What
An erasure request against a customer or traveller anonymises their PII in place while leaving
every financial and audit record intact.

#### ⚠️ Blocked — do not start
This is **[open question 26](../ARCHITECTURE_AND_DELIVERY_PLAN.md)**. The NDPA 2023 right to
erasure conflicts directly with the seven-year retention we need on financial records and audit
logs. The plan's recommendation is anonymisation-with-preservation, which is what this issue
describes — but that is a **legal interpretation, not an engineering decision**, and it needs
Nigerian counsel to confirm it before a line of code is written. Building it first and asking
afterwards risks either a compliance failure or an unrecoverable deletion of financial history.

Labelled `blocked` and `needs-decision` for exactly that reason.

#### Acceptance criteria (assuming the recommendation is confirmed)
- [ ] An erasure request is recorded, audited, and requires a stated reason
- [ ] Anonymisation replaces PII in place: name → a placeholder, email and phone → null or an
      irreversible hash, document numbers destroyed
- [ ] Ledger entries, order lines, invoices and audit rows survive **and still balance** — the
      row keeps its shape, it just stops identifying anyone
- [ ] Uploaded documents removed from blob storage through `IBlobStorage`
- [ ] Irreversible: no shadow copy, no "archived" table holding what was erased
- [ ] Test: after erasure the nightly ledger integrity audit still passes and a historical invoice
      still renders
- [ ] An ADR records the interpretation, who approved it, and when

**Depends on:** #21, #22, #62

---

### S6 · `Security: Security headers, CORS and cookie hardening`

#### What
The response headers and cookie flags that stop a browser doing something on our behalf.

#### Acceptance criteria
- [ ] HSTS with a sensible `max-age`; **preload only after** custom domains are proven, since
      preload is effectively irreversible for an agent's domain
- [ ] `Content-Security-Policy` on the storefront, `X-Content-Type-Options: nosniff`,
      `Referrer-Policy`, `frame-ancestors`, `Permissions-Policy`
- [ ] Refresh token in an `HttpOnly`, `Secure`, `SameSite` cookie; the access token never in
      `localStorage`
- [ ] CORS driven by the **verified custom-domain table**, not a wildcard — `*` with credentials
      is a cross-tenant hole, and the allowlist is data here, not configuration
- [ ] A test asserting the headers are present on both API and storefront responses, so a later
      middleware reordering cannot silently drop them

#### ⚠️ The CSP has a branding trap
A storefront runs on the **agent's** domain. If the CSP names our asset origins, the agent's
customers can read our domain straight out of the response headers — which breaks
[CLAUDE.md rule 4](../../CLAUDE.md#4-nothing-traveller-facing-may-reference-trips) in a place
nobody thinks to look. Serve storefront assets from a neutral origin, or from the agent's own
domain.

**Depends on:** #16, #59, #60

---

### S7 · `Security: Dependency and secret scanning in CI`

#### What
Automated scanning in the CI pipeline, so known-vulnerable packages and committed secrets are
caught by a machine rather than by a person reading a diff.

#### Acceptance criteria
- [ ] `dotnet list package --vulnerable --include-transitive` fails the build on High or Critical
- [ ] `pnpm audit` at the same threshold
- [ ] Dependabot (or Renovate) for NuGet, pnpm **and GitHub Actions** — a compromised action is a
      supply-chain path straight into CI
- [ ] CodeQL for C# and TypeScript on pull requests targeting `main`
- [ ] GitHub secret scanning with push protection enabled — this complements
      `.githooks/pre-commit`, which only protects developers who actually ran `scripts/setup.sh`
- [ ] A documented triage path for the case that will definitely happen: a High advisory on a
      transitive dependency with no fix available yet

#### Notes
Smallest issue here and the first one worth doing. It needs no product code — only
[#7](https://github.com/innovateavitech/trips-agent/issues/7) — so it can land early in M1 and
protect everything built after it, rather than waiting for M3.

**Depends on:** #7

---

### S8 · `Security: Load test the search endpoint, cold and warm cache`

#### What
A repeatable load test of flight search at expected peak, reported separately for a cold and a
warm cache.

#### Acceptance criteria
- [ ] A k6 (or NBomber) scenario checked into `backend/tests/load/`, runnable locally against Docker
      Compose — a load test nobody can re-run is a one-off anecdote
- [ ] Two runs reported separately: **cold** (every request reaches the supplier stub) and
      **warm** (the cache is doing its job)
- [ ] The supplier is a WireMock stub with realistic injected latency. **Never load-test against
      the real Trips Africa API** — see open question 27: they publish no rate limits and no
      concurrency ceiling, so peak load against their staging is an incident waiting to happen
- [ ] Reports p50/p95/p99 and error rate against the FRD §2.3 target of 5s p95, and states
      plainly whether it is met — and whether it is met only warm
- [ ] Measures what the load does to Postgres and Redis: connection pool saturation, cache hit
      ratio, memory
- [ ] Written up with the actual numbers, feeding
      **[open question 16](../ARCHITECTURE_AND_DELIVERY_PLAN.md)** (is the SLA measured
      post-cache?) — that question cannot be answered commercially until this produces a number

#### Notes
"Expected peak" is not defined anywhere yet. Agree a number with the client before starting, and
write it into the issue — otherwise the test proves nothing in particular.

**Depends on:** #33, #40

---

### S9 · `Security: Penetration test — scope, execution and remediation`

#### What
Commission the test, run it, and track the fixes.

#### Acceptance criteria
- [ ] A written scope: which environments, which surfaces (agent console, storefront, API, admin
      console, Hangfire dashboard), and what is explicitly **out** of scope
- [ ] Non-production credentials and seeded test data prepared. The tester must not be able to
      move real wallet balances or issue real tickets — a live ticket cannot easily be refunded
- [ ] **Multi-tenancy is in scope explicitly.** Give the tester two agencies and ask them to
      cross the boundary. It is the highest-impact bug class in this system and a generic web
      test will not look for it
- [ ] Findings land as **individual issues** labelled `module:security` with a severity label —
      not as one PDF nobody can assign
- [ ] Critical and High remediated and retested before launch; Medium and Low triaged with the
      decision recorded
- [ ] A retest confirms the fixes actually landed

#### Notes
Runs last. A penetration test of a system you already know is unfinished tells you what you
already knew, and you pay for the report twice.

**Depends on:** S1–S8 (rewrite with real numbers when created)

---

## What this breakdown found

Three things worth acting on before M3, and one before M1 finishes.

### 1. Nothing owns standing Redis up in the API

Redis is in `docker-compose.yml` ([#2](https://github.com/innovateavitech/trips-agent/issues/2))
and in `.env.example` ([#3](https://github.com/innovateavitech/trips-agent/issues/3)), and the
plan lists it as the store for "search cache, host→tenant map, distributed locks, rate limits".
But **no issue registers a Redis client in the application.** Today the first thing to need one
is [#40](https://github.com/innovateavitech/trips-agent/issues/40), the search cache, and it
would end up owning the connection setup by accident — which means the rate limiter (S1), the
distributed locks in the checkout saga
([#42](https://github.com/innovateavitech/trips-agent/issues/42)) and the host→tenant map
([#59](https://github.com/innovateavitech/trips-agent/issues/59)) all inherit whatever #40
happened to do.

**Suggested:** a small Platform Foundation issue that registers the connection multiplexer,
health check and key-prefixing convention once. It is an hour of work in the right place and a
day of untangling in the wrong one. Raised separately rather than folded in here, because it is
not security work — S1 is just the first place its absence shows.

### 2. S2 is easier if #12 leaves a way to enumerate tenant-scoped tables

The "every `agency_id` table has a policy" test needs a list of tenant-scoped tables. If
[#12](https://github.com/innovateavitech/trips-agent/issues/12) exposes that list — a marker
interface, a convention, or simply querying `information_schema` for the column — the test is
straightforward. If it does not, S2 starts by inventing one. Worth a comment on #12 now, while it
is still unstarted.

### 3. Open questions this epic touches

Per [CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), these are flagged rather than
guessed at:

| # | Question | Effect here |
|---|---|---|
| 16 | Is the 5s p95 search SLA measured post-cache? | S8 produces the number that lets this be answered. Until then S8 reports both figures and asserts neither |
| 18 | PCI scope — is card entry entirely on Paystack's hosted page? | Sets the penetration test scope in S9. If a custom in-page card form ever appears, we move from SAQ-A to SAQ-A-EP and this epic gets substantially bigger |
| 26 | NDPA erasure vs. 7-year financial retention | **Blocks S5 outright.** Needs Nigerian legal counsel |
| 27 | Supplier publishes no rate limits or concurrency ceiling | S8 must load-test a stub, never Trips Africa |

### 4. "Expected peak" is undefined

S8 cannot be written against "expected peak" until someone says what that is in requests per
second. That is a client answer, not an engineering one, and it should be agreed before S8 is
picked up.

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#71](https://github.com/innovateavitech/trips-agent/issues/71)
3. Create S1–S9 with `module:security` + `M3` (S5 also `blocked` + `needs-decision`), rewriting
   the `Depends on:` lines with real numbers
4. Raise the Redis registration issue against Platform Foundation
5. Comment on [#12](https://github.com/innovateavitech/trips-agent/issues/12) about exposing the
   tenant-scoped table list
6. Close #71 as broken down, or keep it open as the tracking epic — repo convention to be decided
   on the first epic, which is this one
7. `./scripts/generate-backlog.sh`
