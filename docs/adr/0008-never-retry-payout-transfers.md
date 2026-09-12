# ADR-0008: Never retry a payout transfer

**Status:** Accepted
**Date:** 2026-09-12
**Deciders:** Tech lead

## Context

[ADR-0003](0003-never-retry-ticket-issuance.md) says the supplier's ticket-issue call is never
retried, because a timeout is an *unknown outcome* rather than a failure and a retry issues a
second real ticket.

Paying an agency out is the same hazard on a different rail, and a worse one.

Four facts about `POST /transfer` at Paystack drive this decision:

1. **It is not idempotent in any way we can rely on.** A `reference` we choose is sent with the
   request and Paystack rejects a duplicate — but that is a uniqueness check on their side, not a
   documented idempotency contract, and it says nothing about what happens when the request never
   reaches them or the response is lost on the way back.
2. **A timeout tells us nothing.** The transfer may be queued, in flight, or never created.
3. **There is no undo.** A duplicated ticket has a supplier to ring and a documented reversal
   window. A duplicated transfer is money in a third party's bank account, and getting it back is
   a legal process with a worse-than-even chance.
4. **Every HTTP resilience library retries by default.** `AddStandardResilienceHandler()`, which
   this codebase uses on the Paystack *payments* client and on the supplier client, would retry
   this one too if it were registered the same way. The dangerous thing here is the default.

## Decision

**We never send a payout transfer twice.**

- `IBankTransfers.InitiateTransferAsync` is called **at most once per payout, ever**. It is
  registered on its own `HttpClient` with **no** resilience handler, so nothing retries it on our
  behalf.
- A payout is moved to `Sending` and **committed** before the call is made. The sender only ever
  acts on a payout in `Approved`, so a process that dies mid-call cannot come back and send again.
- A timeout, a connection failure or a 5xx moves the payout to `OutcomeUnknown` — a state of its
  own, deliberately not `Failed`.
- An unknown outcome is resolved only by `GetTransferAsync`, which asks Paystack what became of
  **our own reference**. That is a read, and it may be retried freely.
- A reference Paystack has never heard of comes back as `Unknown`, not as `Failed`. "We have no
  record of it" and "it did not happen" are different sentences and only one of them is safe to
  act on.
- After `PayoutStatusPoller.MaxStatusQueries` unresolved queries, the payout stops being polled
  and a human is told. Guessing after N attempts is the failure mode this whole ADR exists to
  prevent.

## Options considered

### Option A — Retry the initiate call with backoff, and rely on the reference for idempotency

- ➕ One line of configuration; the same shape as every other HTTP client here.
- ➖ Rests entirely on an undocumented uniqueness guarantee, at the one call where being wrong
  costs real money that cannot be recovered.
- ➖ A retry after a network timeout is exactly the case where the first request may have
  succeeded — which is the case the reference cannot protect against if the first request never
  arrived and the second creates the transfer while the first is still in flight behind it.

### Option B — Mark a timeout as failed and return the money to the wallet

- ➕ Simple, and the agent's balance is never wrong for long.
- ➖ Wrong in the expensive direction. If the transfer did go through, the agency has been paid by
  the bank *and* credited back in the ledger — paid twice, and the books say it never happened.

### Option C — Unknown is its own state, resolved only by asking (chosen)

- ➕ Never sends twice, never invents an outcome.
- ➖ A payout can sit in `OutcomeUnknown` for a while, and somebody has to look at it eventually.
- ➖ One more state for the console to explain to an agent.

## Why we chose what we chose

The asymmetry decides it. A payout stuck in `OutcomeUnknown` for an hour is an inconvenience with
an obvious fix: ask Paystack, and their answer is authoritative. A duplicated transfer is
unrecoverable money and, at the scale this platform is aiming at, an existential-sized mistake
somebody makes once.

Between a state that is annoying and a state that is unrecoverable, the annoying one wins every
time.

## Consequences

- **Good:** money leaves the platform at most once per payout, provably, and the proof is a test
  that counts calls on a stub.
- **Good:** the same discipline as ADR-0003, so there is one rule to learn rather than two.
- **Bad:** payouts need a status poller and a bounded give-up, which is more moving parts than a
  retry policy.
- **Bad:** somebody has to work a queue of unresolved payouts. That is a real operational cost and
  it is the price of the rule.
- **Watch for:** a future contributor "fixing" the missing retry policy. The registration in
  `TripsAgent.Integrations.Paystack.DependencyInjection` carries a comment naming this ADR for
  exactly that reason.
