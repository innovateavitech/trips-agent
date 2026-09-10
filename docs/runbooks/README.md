# Runbooks

A runbook answers one question: **"this specific thing is broken right now — what do I do?"**

They are written for whoever is on support at 11pm, who may not have built the thing that broke.
Assume the reader is capable but has no context and is under pressure.

## Rule

**Every background job gets a runbook before it goes to production.** A job that can fail
silently and has no runbook is an incident waiting to happen, because the only person who knows
how to fix it is the person who wrote it — and they will be asleep.

## Format

Keep each one to a page:

1. **Symptom** — what someone actually notices. "An agent says their ticket never arrived", not
   "the poller has stalled"
2. **How to confirm it** — the exact query, log filter or dashboard that proves this is the
   problem and not something else
3. **Impact** — is money at risk? Is a customer waiting? Can this wait until morning?
4. **Fix** — numbered steps. Copy-pasteable commands
5. **If that doesn't work** — who to escalate to
6. **Prevention** — the issue link for the permanent fix, if there is one

## Planned runbooks

Written as each system goes live.

### Milestone 1

- `ticket-stuck-in-pending.md` — a booking has sat in `TicketPending` past its expected window
- `payment-reversal-failed.md` — the supplier said cancelled, but the refund did not go through
- `wallet-ledger-mismatch.md` — the nightly integrity job found `wallet.balance ≠ SUM(ledger)`. **P1**
- `supplier-api-down.md` — Trips Africa is unreachable or erroring at scale
- `hash-validation-failure.md` — a price-confirmation hash did not match. Possible tampering
- `outbox-backlog.md` — `outbox_messages` is growing and not draining

### Milestone 2

- `ssl-provisioning-failed.md` — an agent's custom domain has no working certificate
- `dns-verification-stuck.md` — an agent added the record but verification keeps failing
- `departure-oversold.md` — capacity went negative. Should be impossible; if it happens, why?
- `storefront-503.md` — an agent's site is down

### Milestone 3

- `report-job-stuck.md` — an async report has been running too long
- `analytics-rollup-drift.md` — dashboard numbers disagree with the source tables
- `subscription-billing-failure.md` — renewals are failing across multiple agents

## Writing a good one

The test: **could someone who has never touched this system follow it successfully?** If a step
says "check the logs", it is not finished — say *which* logs, filtered by *what*, and what a
healthy result looks like versus a broken one.
