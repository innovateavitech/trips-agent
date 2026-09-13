# Handover

Where the Trips Agent Platform stands, and every piece of work left, in one place.
Written 13 September 2026, when PR 4 was opened. [BUILD_PLAN.md](BUILD_PLAN.md) holds the full
detail behind every line here; this page is the index into it.

## Where it stands

The build was organised as four large PRs, each delivering whole features end to end: schema,
API, jobs, screens and tests.

| PR | What it delivered | State |
|---|---|---|
| [#167](https://github.com/innovateavitech/trips-agent/pull/167) | **Milestone 1, the money path.** Flight and bus booking with ticket issuance that is never retried, the status poller, the checkout saga over the wallet and ledger, the resolution queue, branded invoices and vouchers, traveller emails | Merged |
| [#168](https://github.com/innovateavitech/trips-agent/pull/168) | **Milestone 2, the agent's own shop.** Tours, packages and visas, the branded storefront on the agency's own domain, guest checkout and card payment, group departures that cannot oversell, CRM | Merged |
| [#169](https://github.com/innovateavitech/trips-agent/pull/169) | **Milestone 3, running and charging for the platform.** The back office, subscription plans whose limits are enforced, sub-agent networks, analytics and CSV exports, payouts, disputes and reconciliation | Merged |
| PR 4 | **Launch readiness.** Encryption at rest, retention and NDPA erasure, security headers and cookie hardening, the search load test, and an internal adversarial pass that found and fixed six holes | Open from `feat/M4-launch` |

The plan's features stand at: F1 53 of 58 boxes, F2 13 of 14, F3–F7 all, F8 5 of 6, F9 and F10
all, F11 9 of 11, F12 all, F13 1 of 4, F14 46 of 50. Every unticked box is listed below with the
reason it is unticked.

## 1. Launch gates — must happen before real money or real travellers

These are not code. Each needs a person outside the codebase.

1. **External penetration test and its retest** (#110, #71). Commission a tester. The written
   scope is [security/PENETRATION_TEST_SCOPE.md](security/PENETRATION_TEST_SCOPE.md), and the
   recipe for a non-production environment with credentials that grant nothing real is
   [security/TEST_ENVIRONMENT.md](security/TEST_ENVIRONMENT.md). Multi-tenancy is explicitly in
   scope: give the tester two agencies and ask them to reach each other's data. Findings become
   `module:security` issues with a severity; Critical and High are fixed and retested before
   launch. What our own pass already found and fixed is in
   [security/INTERNAL_ADVERSARIAL_PASS.md](security/INTERNAL_ADVERSARIAL_PASS.md).
2. **Turn on GitHub secret scanning with push protection** (#108, its last box). It needs a
   repository admin; the account that built this had push rights only, so the setting could not
   even be read. The repository is public, so both are free:
   ```bash
   gh api -X PATCH repos/innovateavitech/trips-agent --input - <<'JSON'
   {"security_and_analysis":{"secret_scanning":{"status":"enabled"},"secret_scanning_push_protection":{"status":"enabled"}}}
   JSON
   ```
3. **The client's Data Protection Officer, or Nigerian counsel, signs off two things** (open
   question 26, #106):
   - the retention schedule in [DATA_RETENTION.md](DATA_RETENTION.md). Until then the purge job
     and the supplier-log partition job both stay in **dry run**, which is their default — they
     report what they would delete and delete nothing;
   - reading NDPA erasure as anonymisation, recorded in
     [ADR-0009](adr/0009-ndpa-erasure-as-anonymisation.md) as accepted for the MVP *pending that
     sign-off*. The ADR says so rather than naming an approver who has not approved it.
4. **Check three things in Paystack test mode** before the payout and dispute code meets live
   money: transfer OTP is switched **off** on the account (otherwise every payout sits at
   "outcome unknown"); whether a dispute's `refund_amount` arrives in kobo; and the field name of a
   settlement's date. All three are assumptions in the code today.
5. **Run a ticket end to end against Trips Africa staging.** It is Milestone 1's own acceptance
   test. The pipeline is built and tested against recorded payloads and a stub; whoever holds the
   staging credentials should issue one real test ticket, force one failure to prove the reversal,
   and confirm a tampered hash blocks issuance.
6. **Give the load test real numbers and run it again.** It assumed 20 searches a second at peak
   and a log-normal supplier latency (median 1.5 s domestic, 3 s international), because nobody
   had supplied either. On those assumptions search meets the 5 s p95 target — 36 ms warm, 4.3 s
   cold — and our own code accounts for 5 ms of a cold search: the target is decided by the
   supplier, not by us. Replace both assumptions with the client's expected peak and the first
   week of real `supplier_api_calls.latency_ms`; open question 16 asks whether the SLA should be
   measured with the supplier included at all. See [LOAD_TEST_SEARCH.md](LOAD_TEST_SEARCH.md).

## 2. Security findings still open

Found by the internal adversarial pass, one issue each, none Critical or High — those were
fixed in PR 4 with tests.

| Issue | Finding | Severity |
|---|---|---|
| [#170](https://github.com/innovateavitech/trips-agent/issues/170) | An invitation can make a verified account for an address nobody proved | Medium |
| [#171](https://github.com/innovateavitech/trips-agent/issues/171) | A suspended agency still takes trip requests and quote acceptances | Medium |
| [#172](https://github.com/innovateavitech/trips-agent/issues/172) | Any agency user can replace the agency's KYB submission | Low |
| [#173](https://github.com/innovateavitech/trips-agent/issues/173) | Unauthenticated routes that cost CPU or a gateway call are unthrottled | Low |
| [#174](https://github.com/innovateavitech/trips-agent/issues/174) | A traveller's document link never expires and outlives the agency | Low |
| [#175](https://github.com/innovateavitech/trips-agent/issues/175) | Three loose ends: dispute evidence assets, quote tokens stored in plain text, and an allowlist | Low |

The two Mediums are worth fixing before the external test, so the tester does not spend paid time
rediscovering them.

## 3. Decisions waiting on the client

Every open question was answered for the MVP so no work waited (BUILD_PLAN, *Decisions for the
MVP*). These are the answers most worth confirming, because they change behaviour:

- **Who fronts a sub-agent's money** (open question 3). Today a sub-agent pays from its own wallet,
  with its principal's allowance enforced as a cap on top. Drawing on the principal's wallet
  directly needs this answered.
- **Loyalty points and reviews** (open question 24). Only the plan entitlement flag exists; the FRD
  names both with no use case written, so nothing is built behind it.
- **What a newly registered agency may do before it subscribes.** An agency with no plan gets the
  most restrictive entitlements: one published product, no custom domain, no sub-agents. That is
  billing's fallback, and PR 3 made it enforced. Decide whether registration should start agencies
  on a trial plan instead.
- **Retention periods and the erasure reading** — launch gate 3 above.
- **Expected peak and the SLA's meaning** — launch gate 6 above.

## 4. Waiting on the choice of cloud

Everything touching infrastructure goes through a port, so each of these is one adapter behind an
interface that already exists and is already tested:

- **Object storage** — an S3-compatible `IBlobStorage` adapter (#18 was closed with this deferred).
  Development uses local signed-URL storage.
- **Key management** — `IFieldEncryptor` reads its key ring from configuration today. A KMS-backed
  implementation replaces it; the key id is already stored with every ciphertext, so rotation works
  either way ([runbooks/field-encryption.md](runbooks/field-encryption.md)).
- **SSL issuance for custom domains** — a real ACME adapter behind the certificate port; the
  development adapter stands in.
- **Email and SMS delivery** — email goes through `IEmailSender`; SMS and WhatsApp are logged, not
  sent.

## 5. Deliberately left out of the MVP

Recorded feature by feature under *What the MVP leaves out* in the plan. The ones a user will
notice first:

- **Finance back-office screens** for approving payouts, working disputes and triaging
  reconciliation. The API exists and is tested; Finance uses it directly until the screens exist.
- **Bus round trips and a terminal list** (F1, five boxes). The Trips Africa API documents neither;
  one-way bus search works. Supplier-side, not ours to build.
- **Per-product document templates** (F2): one plain template serves tours, visas and departures.
- **Analytics extras** (F8, F11): the top-agent leaderboard, XLSX export and scheduled reports.
- **Payments extras** (F5, F6, F12): automatic charging of saved cards for instalments, scheduled
  automatic payouts, forwarding uploaded evidence files to Paystack (text evidence is filed), and
  posting gateway fees to the ledger (there is no platform expense account yet).
- **Billing**: monthly plans only, no promotions (the column exists, so they need no migration).
- **Storefront**: two site templates and a fixed set of blocks.

## 6. Things an operator must know

- **Deploy with the `migrate` command, never a bare `dotnet ef database update`.** Encrypting
  existing data runs in three steps: add the ciphertext columns, encrypt every row in code (the key
  is in configuration, so SQL cannot), then drop the plaintext. `migrate` runs all three. A bare EF
  update would reach the drop without the encryption in between — and the drop refuses, changing
  nothing, while any row is unencrypted, so the failure is loud rather than lossy.
- **Set every secret that `.env.example` leaves blank or marks as a placeholder**, per environment,
  and never in that file:
  - `Security__FieldEncryption__ActiveKeyId` and one `Security__FieldEncryption__Keys__<id>` per key
    (a base64 32-byte key). Without them the encrypted columns refuse to read or write, by design:
    a missing key is an outage for those columns, never a reason to store a passport number in
    clear.
  - `Security__SecretEncryptionKey`, which protects stored credentials.
  - `Jwt__SigningKey` — `.env.example` ships `dev-only-insecure-key-REPLACE-ME`, and a deployment
    that keeps it lets anyone mint a session.
  - The Paystack, Trips Africa and email credentials in the third-party section.
  - The `Storage__*` settings are unused until the S3-compatible adapter in section 4 exists;
    storage is local files today.
- **The retention jobs start in dry run.** Switch them to live only after launch gate 3.
- **Never add a retry to ticket issuance or payout transfers.** Both calls are not idempotent; a
  timeout is an unknown outcome resolved by asking, never by sending again
  ([ADR-0003](adr/0003-never-retry-ticket-issuance.md),
  [ADR-0008](adr/0008-never-retry-payout-transfers.md)). The payout transfer client has no
  resilience handler on purpose, and a comment there says why.
- **Two tables grow without a purge rule yet**: `rollup_runs` (about 105,000 rows a year) and
  finished report files in blob storage. Neither matters for months; both need a retention entry.
- **The integration suite needs Docker and about 20 minutes** in one pass (1,000+ tests). The test
  database container asks for 1 GB of shared memory; without it the suite fails with
  `53100 … No space left on device`, which looks like a broken migration and is not.

## 7. Where to find things

| | |
|---|---|
| [BUILD_PLAN.md](BUILD_PLAN.md) | Every feature, every acceptance box, every MVP decision and scope cut |
| [CLAUDE.md](../CLAUDE.md) | The eight hard rules: never push to main, money in minor units, never bypass the tenant filter, nothing traveller-facing says Trips, prices frozen at purchase, never retry ticket issuance, design tokens only, never commit secrets |
| [adr/](adr/) | Why things are as they are, including 0003 (issuance), 0006 (row-level security), 0008 (payout transfers), 0009 (erasure) |
| [runbooks/](runbooks/) | Operating procedures: field encryption and key rotation, vulnerable dependencies, the audit log |
| [security/](security/) | The penetration-test scope, its test environment, and the internal pass's findings |
| [DATA_RETENTION.md](DATA_RETENTION.md) | Every table, how long it is kept, and why |
| [LOAD_TEST_SEARCH.md](LOAD_TEST_SEARCH.md) | The load test, how to run it, and its numbers |

## 8. Local housekeeping on the build machine

Nothing here affects the repository; it is clean-up for whoever inherits the laptop.

- Around forty git worktrees under `~/trips-agent-worktrees/` and `.claude/worktrees/`, one per
  feature branch. Every branch is merged or pushed; they can be removed with
  `git worktree remove <path>` and then `git worktree prune`.
- A scratch database, `trips_agent_edge`, on the local Postgres container, left by the storefront
  CSP check. Safe to drop.
- An untracked `backend/services/TripsAgent.Application/Payments/TopUpReceiptEmail.cs` in the main
  checkout, left over from an early branch. Compare it with main before deleting it.
