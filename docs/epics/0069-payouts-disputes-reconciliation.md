# Epic #69 — Payouts, disputes and gateway reconciliation

**Epic:** [#69](https://github.com/innovateavitech/trips-agent/issues/69) ·
**Module:** Payments · **Milestone:** M3 — Network, monetisation & back-office
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Agent bank account capture and verification; payout scheduling and settlement; chargeback and
> dispute workflow with evidence; daily gateway reconciliation matching Paystack settlements to
> our ledger, flagging mismatches.

Twelve issues below. Eleven cover those four strands; the twelfth (`P1`) is a table this epic
owns on paper but which an **M1** job already writes to — see
[the first finding](#1-nothing-creates-reconciliation_runs-or-reconciliation_exceptions-and-an-m1-job-already-writes-to-them).

Everything else in the backlog moves money *in*: a traveller pays, an agent tops up, a ticket is
issued. This epic is the first that moves money **out**, and money leaving is a different risk
class. A bug in a search screen shows the wrong price. A bug here sends ₦4m to the wrong bank
account, and no `git revert` brings it back.

---

## Read this before picking anything up

**None of these can start today**, and the payout strand additionally needs a commercial answer
that nobody has given yet.

The foundations are all still open: [#22](https://github.com/innovateavitech/trips-agent/issues/22)
(ledger), [#23](https://github.com/innovateavitech/trips-agent/issues/23) (wallet),
[#24](https://github.com/innovateavitech/trips-agent/issues/24) (Paystack initialize/verify),
[#25](https://github.com/innovateavitech/trips-agent/issues/25) (webhook receiver),
[#31](https://github.com/innovateavitech/trips-agent/issues/31) (Hangfire),
[#66](https://github.com/innovateavitech/trips-agent/issues/66) (back-office console).

That is not a reason to defer the breakdown. Two things in this document change decisions being
made in **M1**, before anyone touches M3:

1. `reconciliation_runs` and `reconciliation_exceptions` have no owner, and
   [#27](https://github.com/innovateavitech/trips-agent/issues/27) writes to them in M1.
2. The `ledger_accounts.account_type` enum in
   [#22](https://github.com/innovateavitech/trips-agent/issues/22) has no account for money
   leaving the platform. Adding a value to an unwritten enum is free. Adding one to a populated,
   append-only ledger is a migration plus a re-derivation of every balance.

Both are in [what this breakdown found](#what-this-breakdown-found).

### Two boundaries with neighbouring issues

**`disputes` is claimed by two epics.** It appears in the Tables list of both this epic and
[#66](https://github.com/innovateavitech/trips-agent/issues/66) (back-office console). The split
proposed here: **#69 owns the dispute lifecycle** — schema, webhook ingestion, evidence,
accounting — and **#66 owns the console it is operated from**, including the back-office roles
and permissions that decide who may act on one. `P10` is the screen work and is written to sit
inside #66's shell rather than build its own.

**Refunds are not disputes.** [#43](https://github.com/innovateavitech/trips-agent/issues/43) is
the payment reversal worker: *we* decided to give money back, on the supplier's documented rules.
A dispute is the cardholder's bank taking money back whether we agree or not. They share the
`refunds` table's shape and almost nothing else — different trigger, different deadline,
different liability. Do not merge them.

---

## The split

`P1`–`P12` are placeholders. They become real issue numbers when the issues are created, and
every `Depends on:` line must be rewritten to match at that point.

| | Proposed issue | Size | Milestone | Depends on |
|---|---|---|---|---|
| P1 | Reconciliation runs and exceptions schema ⚠️ | ~1 day | **M1** | #22 |
| P2 | Agency bank accounts and Paystack recipient verification | ~2 days | M3 | #19, #24, S3 |
| P3 | Payout schema, request and approval | ~2 days | M3 | #23, #22, P2, #66 |
| P4 | Payout execution via Paystack Transfers 🔴 | ~2 days | M3 | P3, #25 |
| P5 | Payout settlement windows and the scheduling job | ~2 days | M3 | P4, #31 |
| P6 | Bank account and payout screens 🟢 | ~2 days | M3 | #48, P3 |
| P7 | Dispute schema and dispute webhook ingestion | ~2 days | M3 | #25, #41 |
| P8 | Dispute evidence assembly and submission | ~2 days | M3 | P7, #18 |
| P9 | Dispute resolution accounting and liability 🚫 | ~2 days | M3 | P7, #22 — **gated** |
| P10 | Dispute queue screens | ~2 days | M3 | P7, P8, #66 |
| P11 | Daily gateway reconciliation job ⚠️ | ~2 days | M3 | P1, #24, #31 |
| P12 | Reconciliation exception triage and screens | ~2 days | M3 | P11, #66 |

All carry `module:payments`. P1 carries `M1`; the rest carry `M3`. P9 additionally carries
`blocked` and `needs-decision`. P4 carries the 🔴 money-path label.

**Order.** `P1` first and soon — it is one day of work in M1 that stops
[#27](https://github.com/innovateavitech/trips-agent/issues/27) from inventing a schema this epic
then has to live with.

After that, the four strands are largely independent and can run in parallel:

```
P1 ──┬──▶ P11 ──▶ P12                    reconciliation  (start here — highest value, fewest deps)
     │
     └──▶ P2 ──▶ P3 ──▶ P4 ──▶ P5        payouts         (P3 onward gated on open question 2)
                    └──▶ P6

          P7 ──┬──▶ P8 ──▶ P10           disputes
               └──▶ P9  🚫
```

**Do the reconciliation strand first.** It is the only one with no commercial dependency, and it
is the strand that tells you the other two are working. A payout system with no reconciliation is
a system that pays out wrongly and does not notice.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### P1 · `Wallet & Ledger: Reconciliation runs and exceptions schema` ⚠️

> **Note the milestone.** This is the one issue in the #69 breakdown that belongs to **M1**, not
> M3. It is listed here because #69 owns the tables on paper, but
> [#27](https://github.com/innovateavitech/trips-agent/issues/27) writes to them in M1 and
> nothing creates them. Create it under `module:wallet-ledger` with the `M1` milestone and make
> **#27 depend on it**.

#### What
`reconciliation_runs` and `reconciliation_exceptions` — the tables every reconciler in the system
writes its findings to.

#### Why it is its own issue, and why now
Three separate jobs produce reconciliation exceptions, and they arrive across two milestones:

| Job | Issue | Milestone | Exception type |
|---|---|---|---|
| Nightly ledger integrity audit | [#27](https://github.com/innovateavitech/trips-agent/issues/27) | M1 | imbalance, wallet drift, orphaned entry |
| Daily gateway reconciliation | `P11` | M3 | `missing_in_ledger`, `amount_mismatch` |
| Supplier booking reconciliation | *(unowned — see findings)* | — | `orphan_supplier_booking` |

If #27 creates the table it needs and nothing more, the shape it lands on will be the shape a
nightly balance check happens to want. P11 then either bends to fit it or migrates it. Defining
the table once, up front, for all three consumers costs a day and removes that entirely.

#### Acceptance criteria
- [ ] `reconciliation_runs`: `type`, `started_at`, `completed_at`, `status`, `window_start`,
      `window_end`, counts of records examined / matched / exceptions raised
- [ ] `reconciliation_exceptions`: `run_id`, `type (missing_in_ledger|amount_mismatch|orphan_supplier_booking|ledger_imbalance|wallet_drift)`,
      `expected_minor`, `actual_minor`, `reference_type`, `reference_id`,
      `status (open|investigating|resolved|written_off)`, `resolved_by`, `resolution_note`,
      `occurred_at`
- [ ] `expected_minor` and `actual_minor` are `bigint` minor units — the whole point of this table
      is comparing two amounts, and a `decimal` here would manufacture the mismatches it exists
      to detect
- [ ] **`agency_id` is nullable and the tenant filter accounts for that.** A gateway settlement
      that matches no ledger entry may not be attributable to any agency — that is precisely what
      makes it an exception. A `NOT NULL` column here forces a fake value onto the rows that
      matter most
- [ ] Indexed on `(status, occurred_at DESC)` for the triage queue and on `(run_id)`
- [ ] An exception is **never deleted**; it is resolved or written off, with a stated reason
- [ ] A run that crashes leaves a `failed` row, not an absent one — a reconciliation that silently
      did not happen must be visibly distinguishable from one that found nothing

**Depends on:** #22

---

### P2 · `Payments: Agency bank accounts and Paystack recipient verification`

#### What
`agency_bank_accounts` — where an agent tells us which account to pay, and where we prove the
account is real and belongs to them before we ever send money to it.

#### Why verification is the whole issue
Capturing an account number is a form. The work here is everything that stops that form being a
fraud vector: an employee substituting their personal account, a typo sending a settlement to a
stranger, or an attacker who has taken over an agent's login quietly changing the payout
destination and waiting.

#### Acceptance criteria
- [ ] `agency_bank_accounts`: `agency_id`, `bank_code`, `bank_name`, `account_number_encrypted`,
      `account_name_resolved`, `recipient_code`, `status (pending|verified|rejected|disabled)`,
      `verified_at`, `is_default`
- [ ] The account number is resolved with the gateway (account number + bank code → the name the
      bank holds) and **the resolved name is stored, not the name the agent typed**
- [ ] The resolved name is compared against the verified KYB business name from
      [#19](https://github.com/innovateavitech/trips-agent/issues/19). A mismatch does not fail
      outright — a legitimate trading name often differs from the registered one — it routes to
      **manual review** and the account stays `pending`
- [ ] A gateway transfer recipient is created and its `recipient_code` stored. Payouts address the
      recipient code, never a raw account number
- [ ] `account_number_encrypted` uses the `IFieldEncryptor` port from the security epic's `S3`
      (see [#71's breakdown](0071-security-hardening.md)) — **not** a second encryption
      implementation invented here
- [ ] Only the last four digits are ever returned to the console or written to a log
- [ ] Changing or adding an account is audited via
      [#21](https://github.com/innovateavitech/trips-agent/issues/21) with the actor and a reason,
      and **notifies the agency's registered owner out of band** — if the change is an account
      takeover, an email to the address the attacker now controls is not a notification
- [ ] A newly added or changed account is subject to a **cooling-off period** before it can
      receive its first payout, configurable and defaulting to 24 hours
- [ ] Only one `is_default` account per agency per currency, enforced by a partial unique index
- [ ] An account can be disabled but never hard-deleted — historical payouts must keep resolving
      to the account that received them

#### Notes
Bank codes are gateway-specific and change. Fetch the bank list from the gateway and cache it;
do not check a hard-coded list into the repo.

**Depends on:** #19, #24, `S3` (security epic)

---

### P3 · `Payments: Payout schema, request and approval`

#### What
`payouts` — an agent asks for their money, somebody approves it, and the ledger records the
obligation. This issue stops at approved. `P4` is what actually sends it.

#### Why request and execution are separate issues
Because the approved-but-not-yet-sent state is where every control lives. Splitting them means
`P4` can be written as a dumb executor of already-authorised instructions, which is exactly what
you want the money-moving code to be.

#### ⚠️ Gated on open question 2
How much of this matters depends on **who the merchant of record is**
([open question 2](../ARCHITECTURE_AND_DELIVERY_PLAN.md)). Read that before designing the flow:

- **Platform as merchant of record** (the plan's recommendation) — customer money lands with us,
  sits in the agent's wallet, and this table is the *only* way it reaches the agent. Payouts are
  load-bearing.
- **Agent's own subaccount collects directly** — the gateway settles to the agent without us, and
  payouts shrink to withdrawals of wallet top-up remainders.

Note these are not mutually exclusive: even under split settlement, an agent who pre-funds their
wallet under [open question 3](../ARCHITECTURE_AND_DELIVERY_PLAN.md)(a) has our-held money that
needs a way out. **Build the table either way; scope the volume expectations after the answer.**

#### Acceptance criteria
- [ ] `payouts`: `agency_id`, `bank_account_id`, `amount_minor`, `fee_minor`, `net_minor`,
      `currency`, `status (requested|approved|processing|paid|failed|reversed)`, `requested_by`,
      `approved_by`, `gateway_transfer_ref`, `idempotency_key UNIQUE`, `failure_reason`,
      timestamps per transition
- [ ] **The amount is checked against `wallets.available_balance_minor`, not `balance_minor`** —
      see the hazard note below. This is the single most important line in this issue
- [ ] Requesting a payout places a `wallet_holds` row for the amount, so two concurrent requests
      cannot both pass the balance check. Released on rejection, captured on payment
- [ ] Approval is a **different user** from the requester, and the approving role is the Finance
      back-office role from [#66](https://github.com/innovateavitech/trips-agent/issues/66)
- [ ] Balanced ledger entries on approval, against the outbound payout account — see
      [finding 2](#2-the-ledger-has-no-account-for-money-leaving-the-platform)
- [ ] Refused outright while the wallet is `frozen`, while KYB is not `verified`, or while the
      bank account is `pending` or inside its cooling-off period
- [ ] A configurable minimum payout amount, and a maximum above which approval escalates
- [ ] Every transition audited with actor and reason via
      [#21](https://github.com/innovateavitech/trips-agent/issues/21)
- [ ] Concurrency test: two simultaneous payout requests totalling more than the available
      balance — exactly one succeeds

#### 🚩 The hazard: available balance is not balance
`wallets` carries `balance_minor`, `available_balance_minor` and `reserved_minor` precisely
because a booking in flight has money spoken for but not yet spent. Pay out against
`balance_minor` and you can hand an agent money that a `wallet_hold` has already reserved for a
ticket about to be issued. The checkout saga then fails at issue time, **inside the
`TicketTimeLimit`**, on a booking the traveller has already paid for — and the money that should
have covered it is in a bank account you cannot claw it back from.

**Depends on:** #23, #22, `P2`, #66

---

### P4 · `Payments: Payout execution via Paystack Transfers` 🔴

#### What
The worker that takes an approved payout and actually sends the money.

#### 🔴 Never retry a transfer on timeout
This is [CLAUDE.md rule 6](../../CLAUDE.md#6-never-retry-the-suppliers-ticket-issue-call) in a
second place. A transfer initiation that times out is an **unknown outcome, not a failure.**
Retrying it can send the money twice, and unlike a duplicated ticket there is no supplier to
call — the funds are in a third party's bank account and recovery is a legal process.

Resolve an unknown outcome by **querying the transfer's status by our own reference**, never by
re-initiating. Same shape as ADR-0003, different rail. This issue should produce
[an ADR of its own](#3-payout-execution-needs-its-own-adr).

#### Acceptance criteria
- [ ] Our `payouts.idempotency_key` is sent as the gateway's transfer `reference`, so a
      re-initiation of the *same* payout is rejected by the gateway rather than duplicated by it.
      This is the backstop, not the plan
- [ ] **No Polly retry policy on the initiate call.** A timeout transitions the payout to a
      terminal-unknown state and schedules a status query. Write this as an explicit comment
      naming the rule, so a well-meaning future contributor does not "fix" the missing retry
- [ ] A status poller resolves `processing` payouts by reference, with backoff, and gives up to a
      **manual** admin alert rather than guessing after N attempts
- [ ] Transfer outcome webhooks (success / failed / reversed) are handled through the existing
      `payment_webhook_events` idempotency from
      [#25](https://github.com/innovateavitech/trips-agent/issues/25) — not a second webhook path
- [ ] Poller and webhook can both resolve the same payout and produce **one** set of ledger
      entries. Test this explicitly: it is the realistic race, not a theoretical one
- [ ] `failed` releases the wallet hold and returns the funds to available balance, with balanced
      reversing entries — never by editing the original entries
- [ ] `reversed` (the bank returned it days later) is handled as a distinct state, not as
      `failed`. The money left and came back; both legs belong in the ledger
- [ ] The gateway's own balance is checked before initiating, and an insufficient platform balance
      raises an operational alert rather than failing the agent's payout as if it were their fault
- [ ] Contract tests against a stubbed gateway for: success, insufficient balance, invalid
      recipient, timeout-then-success, timeout-then-failure

#### Notes
Some gateways require OTP confirmation on transfers by default, which cannot work for an
unattended worker. Whether OTP is disabled on the account is an **operational prerequisite**, not
a code change — confirm it before starting and write the answer into
[the Paystack notes](#4-there-are-no-paystack-api-notes-in-the-repo).

**Depends on:** `P3`, #25

---

### P5 · `Payments: Payout settlement windows and the scheduling job`

#### What
Automatic payouts on a schedule, so agents are not filing a manual request every week.

#### Acceptance criteria
- [ ] A per-agency settlement window — `manual`, `weekly` (with a day) or `monthly` (with a date)
      — defaulting to `manual`. **Automatic payout is opt-in**, because the failure mode of an
      unrequested transfer is worse than the inconvenience of asking for one
- [ ] A Hangfire job that creates `requested` payouts for due agencies, from
      [#31](https://github.com/innovateavitech/trips-agent/issues/31)
- [ ] The job **creates requests; it does not approve them.** Scheduling automates the paperwork,
      not the authorisation. If auto-approval below a threshold is wanted later, that is a
      deliberate decision with its own issue and its own audit trail
- [ ] Skips silently — with a recorded reason — when the balance is under the minimum, KYB has
      lapsed, the wallet is frozen, or no verified bank account exists
- [ ] **Idempotent per window.** Running twice on the same day produces one payout per agency, not
      two. Enforced by a unique constraint on `(agency_id, window_start)`, not by the job
      remembering it already ran
- [ ] A run that partially fails does not roll back the agencies it already processed, and picks
      up cleanly where it stopped
- [ ] Nigerian bank holidays and weekends: document whether a due date lands early, late, or
      unchanged. Any answer is fine; an undocumented one produces a support ticket every quarter
- [ ] Test at 500 agencies that the job completes and holds no long transaction while doing it

**Depends on:** `P4`, #31

---

### P6 · `Payments: Bank account and payout screens` 🟢

#### What
The agent-console screens for the two things an agent does here: tell us where to pay them, and
ask to be paid.

#### Acceptance criteria
- [ ] Bank account: add, view, set default, disable. Account numbers shown as last-four only
- [ ] The verification state is **visible and explained** — `pending`, `in manual review`,
      `verified`, and how long a cooling-off period has left. An agent whose payout is blocked
      should learn why from the screen, not from support
- [ ] Payout request form showing available balance, and showing it as *available*, distinct from
      total balance, with the reserved amount explained. This is the number agents will query most
- [ ] Payout history with status, amount, destination last-four and failure reason
- [ ] Failure reasons are rendered in plain language, not as a raw gateway error string
- [ ] Built from `packages/ui` components and design tokens only — no hard-coded colour, per
      [CLAUDE.md rule 7](../../CLAUDE.md#7-never-hard-code-a-colour-font-or-spacing-value).
      `pnpm check:design` passes
- [ ] Empty, loading and error states for every list

#### Notes
Marked `good-first-issue` deliberately: it is real, visible work with no money-moving code behind
it. The API it calls (`P3`) does the dangerous part.

**Depends on:** #48, `P3`

---

### P7 · `Payments: Dispute schema and dispute webhook ingestion`

#### What
`disputes` — a cardholder has told their bank the charge was wrong, and a clock has started.

#### Acceptance criteria
- [ ] `disputes`: `agency_id`, `payment_transaction_id`, `order_id`, `gateway_dispute_ref UNIQUE`,
      `category`, `amount_minor`, `status`, `due_by`, `evidence jsonb`, `resolution`, `resolved_at`
- [ ] Dispute webhooks (created / reminder / resolved) ingested through the existing
      `payment_webhook_events` dedup from
      [#25](https://github.com/innovateavitech/trips-agent/issues/25)
- [ ] **A dispute arrives for a transaction we may not recognise.** Do not drop it and do not
      throw — record it unlinked and raise an exception for triage. An unrecognised chargeback is
      information, and losing it costs the money it was about
- [ ] `agency_id` is resolved from the payment transaction, so the dispute is tenant-scoped and an
      agent sees only their own
- [ ] Raises an `admin_alert` of type `dispute` on arrival, and again as `due_by` approaches
- [ ] The agency is notified through
      [#45](https://github.com/innovateavitech/trips-agent/issues/45) — they hold the evidence,
      and they cannot supply it if nobody tells them
- [ ] **`due_by` is stored and honoured as an absolute instant in UTC.** Miss it and the dispute is
      lost by default regardless of the merits, so this is not a field to be clever with
      timezones on
- [ ] A reminder job escalates as the deadline nears; the escalation thresholds are configuration
- [ ] Test: the same dispute event delivered five times produces one row and one alert

**Depends on:** #25, #41

---

### P8 · `Payments: Dispute evidence assembly and submission`

#### What
Collecting what proves the charge was legitimate, and sending it to the gateway before `due_by`.

#### Why most of it can be automatic
For a travel booking we already hold, in our own tables, most of what a card scheme asks for: the
order, what was sold, the traveller name, the delivery of the ticket or voucher, the PNR, the
issuance timestamp, and the agent's own terms. Assembling that automatically and letting the
agent add to it beats a blank upload box, which is what an agent under deadline pressure will
ignore.

#### Acceptance criteria
- [ ] Evidence is auto-assembled from the order: line items, traveller, amounts, the generated
      invoice and voucher from [#46](https://github.com/innovateavitech/trips-agent/issues/46),
      supplier confirmation reference, and the delivery timestamps
- [ ] The agent can add free-text and upload files, via the asset pipeline in
      [#18](https://github.com/innovateavitech/trips-agent/issues/18) — including its malware
      scan. **Never forward an unscanned agent-supplied file to a third party**
- [ ] Submission to the gateway is recorded with what was sent and when, and the payload is
      retained. When a dispute is lost, "what did we actually submit" is the first question
- [ ] Submission after `due_by` is blocked with a clear message rather than attempted and failed
- [ ] Evidence submission is **idempotent**, and re-submission before the deadline is supported —
      agents will send a first pass and then find the better document
- [ ] Evidence carries traveller PII, so it falls under the retention schedule in the security
      epic's `S4`. Note it there rather than inventing a second policy
- [ ] **Nothing in the evidence bundle references Trips**
      ([rule 4](../../CLAUDE.md#4-nothing-traveller-facing-may-reference-trips)) — see the trap
      below

#### ⚠️ A branding trap, and a real conflict
Dispute evidence is read by the cardholder's issuing bank, and often shown to the cardholder. The
documents in it are the **agent's** invoice and voucher, under the agent's brand — as they must be.

But if the platform is merchant of record
([open question 2](../ARCHITECTURE_AND_DELIVERY_PLAN.md)), the **descriptor on the cardholder's
statement is ours, not the agent's** — which is very likely what caused the dispute in the first
place ("I don't recognise this charge"). Evidence must then explain a relationship the traveller
was never meant to see.

That is not a bug this issue can fix; it is a consequence of the merchant-of-record answer, and it
should be raised as a **product** question when open question 2 is decided. Flagged here because
this is where it first becomes concrete.

**Depends on:** `P7`, #18

---

### P9 · `Payments: Dispute resolution accounting and liability` 🚫

#### What
When a dispute is won or lost, the ledger records it — and someone bears the loss.

#### 🚫 Gated — do not start
Who eats a lost chargeback is **[open question 2](../ARCHITECTURE_AND_DELIVERY_PLAN.md)**, and it
is a commercial and legal question, not an engineering one:

- **The agent**, debited from their wallet — but their wallet may be empty, and now the platform
  is an unsecured creditor of a business that just lost a chargeback.
- **The platform**, absorbed as a cost of being merchant of record.
- **Split**, by category or value.

Every option is a different set of ledger postings against different accounts, and the postings
are append-only. Guessing and correcting later means reversing entries across every dispute
resolved under the wrong assumption — while the agent-facing statements those entries produced
have already been read.

The plan's own note on open question 2 says it *"determines chargeback liability"* and *"needs
legal sign-off, not just product sign-off."* Labelled `blocked` and `needs-decision` for exactly
that reason.

Note that it is scheduled to be answered **before M2 begins**, and this is M3 work — so the gate
should lift naturally. Isolating it into its own issue means `P7`, `P8` and `P10` are not held up
if it does not.

#### Acceptance criteria (once liability is decided)
- [ ] Balanced ledger entries for each outcome: won, lost, and accepted-without-contest
- [ ] The provisional debit at dispute creation and the final posting at resolution are distinct
      events — the money is typically held when the dispute opens, not when it resolves
- [ ] A lost dispute against an insufficiently funded wallet produces a **negative available
      balance and an operational alert**, not a silent failure and not a refusal to post. The loss
      happened; the books must show it
- [ ] Gateway dispute fees posted separately from the disputed amount — they are a different cost
      with potentially a different bearer
- [ ] A won dispute reverses the provisional debit, and the reversal is a new entry, never an edit
- [ ] The agent's statement shows the whole sequence legibly: held, then returned or lost
- [ ] An ADR records which liability model was chosen, who approved it, and when

**Depends on:** `P7`, #22

---

### P10 · `Payments: Dispute queue screens`

#### What
The back-office queue for working disputes, and the agent-side view of their own.

#### Acceptance criteria
- [ ] Back-office queue **inside the
      [#66](https://github.com/innovateavitech/trips-agent/issues/66) console shell**, using its
      role-scoped permissions — not a parallel admin surface
- [ ] Sorted by `due_by` ascending by default, because that is the only ordering that reflects
      what is actually urgent, and visibly flagged as the deadline approaches
- [ ] Filters by status, agency and category; a detail view showing the linked order, the
      transaction, the assembled evidence and the full timeline
- [ ] Agent-side view of their own disputes in the agent console, with the evidence upload from
      `P8`. **Tenant-filtered** — this is cross-agency data in the back office and must not become
      cross-agency data in the agent console
- [ ] Unlinked disputes (`P7`) surface in the back-office queue as their own filter, so they are
      triaged rather than invisible
- [ ] Design tokens only; `pnpm check:design` passes

**Depends on:** `P7`, `P8`, #66

---

### P11 · `Payments: Daily gateway reconciliation job` ⚠️

#### What
The daily job that pulls what the gateway says it settled, matches it against our ledger, and
writes down every difference.

#### Why this is the most valuable issue in the epic
Everything else here is a feature. This is the thing that tells you the features are lying. Under
[open question 3](../ARCHITECTURE_AND_DELIVERY_PLAN.md) money is in flight for a day between
checkout and settlement, and without this job a systematic discrepancy — a fee we are not
accounting for, a settlement we credited twice, a payment we never credited at all — accumulates
silently until someone happens to look.

#### Acceptance criteria
- [ ] A Hangfire job pulling settlements and their constituent transactions for a settled window,
      **paginating fully** — a job that reads the first page and declares itself finished reports
      a clean reconciliation while missing most of the data
- [ ] Matched to `payment_transactions` by `gateway_reference`, and to the ledger via
      `gateway_clearing`
- [ ] Three-way arithmetic asserted per settlement: gross − fees = net, and the sum of the
      constituent transactions equals the settlement total. **A discrepancy of one kobo is an
      exception**, not a rounding tolerance. Tolerance thresholds are how a systematic leak stays
      invisible
- [ ] Exceptions written to `reconciliation_exceptions` from `P1`, typed `missing_in_ledger` or
      `amount_mismatch`
- [ ] Gateway fees posted to the ledger as their own entries. They are a real cost and are
      currently accounted for nowhere — see
      [finding 5](#5-gateway-fees-are-modelled-but-never-posted)
- [ ] **Idempotent and re-runnable for any past date.** Re-running yesterday produces the same
      result and no duplicate exceptions. You will re-run it, on the day something looks wrong
- [ ] A run over a window with no settlements completes successfully and records that it found
      nothing — distinguishable from not having run
- [ ] The window has a **lag**: reconcile settled days, not today. Reconciling a day still in
      progress generates exceptions for transactions that simply have not settled yet, and a queue
      full of false positives is a queue nobody reads
- [ ] Any exception raises an operational alert; a run that fails outright raises a higher one
- [ ] Test with seeded fixtures for each case: clean match, missing in ledger, amount mismatch,
      duplicate settlement line, and a settlement spanning midnight

#### 🚩 Do not "fix" a mismatch automatically
It is tempting to have the job correct small differences it finds. It must not. A reconciler that
writes to the ledger it reconciles cannot detect its own errors, and an automatic correction
destroys the evidence of what actually went wrong. **This job reads and reports. Corrections are
human decisions with their own audit trail** — that is `P12`.

**Depends on:** `P1`, #24, #31

---

### P12 · `Payments: Reconciliation exception triage and screens`

#### What
The queue where a human works through what `P11` and
[#27](https://github.com/innovateavitech/trips-agent/issues/27) found.

#### Acceptance criteria
- [ ] Back-office queue inside the
      [#66](https://github.com/innovateavitech/trips-agent/issues/66) shell: filter by type,
      status, date and amount; sort by value, because a ₦2m exception matters more than forty ₦50
      ones
- [ ] A detail view showing both sides of the mismatch — what the gateway said, what our ledger
      says, and the linked transaction, order and settlement
- [ ] Resolve or write off, both requiring a **stated reason**, both audited via
      [#21](https://github.com/innovateavitech/trips-agent/issues/21), and both restricted to the
      Finance role
- [ ] A correcting ledger entry made from here is a **new balanced transaction group** with the
      exception id as its reference — never an edit, per
      [#22](https://github.com/innovateavitech/trips-agent/issues/22)
- [ ] Ageing is visible: an exception open for thirty days is a different problem from one raised
      this morning, and the screen should say so
- [ ] A summary the Finance role can actually use as a control: open exception count and total
      value by type
- [ ] Bulk write-off is **not** in scope. If it is wanted later it needs its own issue and its own
      argument — a button that clears the queue in one click will eventually be used to clear the
      queue in one click
- [ ] Design tokens only; `pnpm check:design` passes

**Depends on:** `P11`, #66

---

## What this breakdown found

Seven things. The first two need acting on **during M1**, before this epic starts.

### 1. Nothing creates `reconciliation_runs` or `reconciliation_exceptions`, and an M1 job already writes to them

[#27](https://github.com/innovateavitech/trips-agent/issues/27) (nightly ledger integrity audit,
**M1**) has the acceptance criterion *"Discrepancies written to `reconciliation_exceptions`"*. It
depends only on [#23](https://github.com/innovateavitech/trips-agent/issues/23). No issue in the
backlog creates that table — the plan lists it under §2.9, and the only epic that claims it is
this one, in **M3**.

So as written, whoever picks up #27 invents the schema in passing, shaped by what a nightly
balance check happens to need, and P11 inherits it a milestone later.

**Suggested:** create `P1` now, under `module:wallet-ledger` with the `M1` milestone, and add it
to #27's dependencies. It is a day of work, it belongs to M1 regardless of which epic noticed it,
and it is the difference between one table designed for three consumers and one table designed
for one.

**The same pattern applies to `admin_alerts`**, which
[#25](https://github.com/innovateavitech/trips-agent/issues/25),
[#27](https://github.com/innovateavitech/trips-agent/issues/27) and
[#43](https://github.com/innovateavitech/trips-agent/issues/43) all raise alerts into in M1, while
the only issue listing it as a table is
[#66](https://github.com/innovateavitech/trips-agent/issues/66) in M3. Not this epic's table, so
not fixed here — worth raising on #66's breakdown when it happens.

### 2. The ledger has no account for money leaving the platform

[#22](https://github.com/innovateavitech/trips-agent/issues/22) fixes
`ledger_accounts.account_type` as `agency_wallet`, `platform_revenue`, `supplier_payable`,
`customer_receivable`, `gateway_clearing`, `tax_payable`, `refunds`.

A payout debits `agency_wallet` and credits — what? The only candidate is `gateway_clearing`, but
that account is already carrying inbound money the gateway owes us. Post outbound payouts to it
too and its balance becomes the *net* of money coming in and money going out, which cannot be
reconciled against anything: `P11` needs to know what the gateway owes us, and that number no
longer exists. The same gap applies to the provisional hold on an open dispute in `P9`.

**Suggested:** add `payout_clearing` and `dispute_holding` to the enum in
[#22](https://github.com/innovateavitech/trips-agent/issues/22) **now**, while it is unstarted.
#22's own criteria make `ledger_entries` append-only with `UPDATE` and `DELETE` revoked — adding
an account type after entries exist means a migration plus re-deriving balances that were posted
to the wrong account. Free today, expensive in three months.

### 3. Payout execution needs its own ADR

[ADR-0003](../adr/0003-never-retry-ticket-issuance.md) exists because retrying a non-idempotent
money-moving call is the most expensive mistake in this system, and because the reasoning has to
survive the person who worked it out. Payout transfers are the same hazard on a different rail,
and arguably worse: a duplicated ticket has a supplier to call, while a duplicated transfer is in
a stranger's bank account.

`P4` describes the rule. **Suggested:** it also produces
`docs/adr/0004-never-retry-payout-transfers.md`, so the next person to see a money-moving call
with no retry policy finds the reason instead of an oversight.

### 4. There are no Paystack API notes in the repo

[docs/TRIPS_AFRICA_API_NOTES.md](../TRIPS_AFRICA_API_NOTES.md) exists because the supplier's
behaviour is full of things you must know before writing code. Paystack has no equivalent — and
five issues here (`P2`, `P4`, `P5`, `P7`, `P11`) depend on gateway behaviour nobody has written
down: whether transfer OTP is disabled on our account, how settlement pagination works, what the
dispute categories and evidence requirements are, the fee structure, and which webhook events
actually fire.

**Suggested:** `docs/PAYSTACK_API_NOTES.md`, started alongside
[#24](https://github.com/innovateavitech/trips-agent/issues/24) rather than by this epic — #24 and
#25 are the first to need it and are a milestone earlier. Raised separately.

### 5. Gateway fees are modelled but never posted

`payment_transactions` carries `fee_minor` and `net_minor`
([#24](https://github.com/innovateavitech/trips-agent/issues/24) records them), but **no issue in
the backlog posts a gateway fee to the ledger.** Nothing balances, because nothing is written at
all — the fee simply does not appear in the books.

It surfaces here because `P11` cannot reconcile without it: the gateway settles *net* of fees, so
matching a settlement to a ledger that only knows gross amounts produces an `amount_mismatch` on
every single transaction. `P11` therefore posts the fees, and that is the right place — a fee is
knowable only once the gateway states it. But it is worth a comment on #24 so it is a decision
rather than an omission discovered in M3.

### 6. Non-NGN wallets have no payout path

`wallets` is `UNIQUE (agency_id, currency)`, so an agency can hold several currency wallets.
`agency_bank_accounts` in `P2` resolves against Nigerian banks and pays in NGN. A wallet in any
other currency therefore accrues a balance with no way out, and `P3`'s balance check is
per-currency.

This is [open question 17](../ARCHITECTURE_AND_DELIVERY_PLAN.md) (multi-currency and FX), whose
recommendation is that MVP sells only in the agent's base currency. **If that holds, this is a
non-issue** — and `P3` should simply refuse a payout request for any currency without a payout
rail, with a clear message, rather than silently having no path. Noted so the constraint is
deliberate.

### 7. Open questions this epic touches

Per [CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), these are flagged rather than
guessed at:

| # | Question | Effect here |
|---|---|---|
| 2 | Merchant of record, and therefore chargeback liability | **Gates `P9` outright** and sets the scope of `P3`–`P5`. Needs legal sign-off. Also creates the statement-descriptor conflict in `P8` |
| 3 | Who fronts money between checkout and ticketing | Sets the settlement lag `P11` reconciles across, and how much agent money sits in our wallets waiting to be paid out |
| 4 | Is the platform fee added to the price or deducted from margin | Changes what `P11` expects a settlement to net down to |
| 17 | Multi-currency and FX | Finding 6 — whether a non-NGN payout rail is ever needed |
| 25 | Merchant of record for VAT and invoicing | Affects what a dispute evidence bundle in `P8` may legitimately claim |
| 26 | NDPA erasure vs. 7-year retention | Dispute evidence in `P8` holds traveller PII on a 7-year financial record. Same conflict as the security epic's `S5` |

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#69](https://github.com/innovateavitech/trips-agent/issues/69)
3. **Create `P1` first**, as an `M1` `module:wallet-ledger` issue, and add it to
   [#27](https://github.com/innovateavitech/trips-agent/issues/27)'s dependencies — this one is
   time-sensitive in a way the rest are not
4. Comment on [#22](https://github.com/innovateavitech/trips-agent/issues/22) proposing
   `payout_clearing` and `dispute_holding` on the account-type enum, while it is still unstarted
5. Comment on [#24](https://github.com/innovateavitech/trips-agent/issues/24) about posting
   gateway fees to the ledger, and raise `docs/PAYSTACK_API_NOTES.md`
6. Create `P2`–`P12` with `module:payments` + `M3` (P9 also `blocked` + `needs-decision`, P4 the
   money-path label, P6 `good-first-issue`), rewriting the `Depends on:` lines with real numbers
7. Raise the merchant-of-record statement-descriptor conflict (`P8`) as a product question when
   open question 2 is answered
8. `./scripts/generate-backlog.sh`
