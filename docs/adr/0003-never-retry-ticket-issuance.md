# ADR-0003: Never retry the supplier's ticket-issue call

**Status:** Accepted
**Date:** 2026-09-10
**Deciders:** Tech lead

## Context

Issuing a flight or bus ticket goes through the Trips Africa endpoint
`POST /api/v2/ticketing/issue`.

Three facts about that endpoint drive this decision:

1. **It is not idempotent.** There is no idempotency key in the request, and the documentation
   makes no guarantee about what happens if the same `SessionId` is submitted twice.
2. **It is slow.** It talks to a GDS, which talks to an airline. Multi-second responses are
   normal and timeouts happen.
3. **It has no webhook.** Success often returns `BookingStatus: "TicketPending"` rather than
   `"TicketIssued"`, so even a 200 response does not tell us the final outcome. The only way to
   learn that is to poll `GET /Flight/GetBookingStatus`.

Standard practice for a flaky HTTP call is to wrap it in a retry policy. Every HTTP resilience
library defaults to this, and it would be the natural thing for a developer to add.

**Here, that default is dangerous.** If the request succeeded and the response was lost, a retry
issues a *second* real ticket, on a real airline, for a real person, paid for with money the
customer never agreed to spend. Refunding it means an offline cancellation process — and the
supplier does not even document flight cancellation.

## Decision

**The `issue` call has retries disabled. Zero. A timeout is treated as an *unknown outcome*, not
a failure, and is resolved by calling `GetBookingStatus` — never by re-issuing.**

## Options considered

### Option A — Standard retry with exponential backoff

- ➕ Simple, matches every other HTTP call in the codebase
- ➖ Can issue duplicate tickets. Unacceptable at any frequency

### Option B — Retry, then detect and cancel duplicates

- ➕ Recovers from transient network failures automatically
- ➖ Detection is racy, and there is no documented flight-cancellation endpoint to clean up with.
  We would be building an automated way to create problems we cannot automatically fix

### Option C — No retries; resolve unknowns by polling status *(chosen)*

- ➕ A duplicate ticket becomes structurally impossible
- ➖ Slower recovery — resolution takes seconds to minutes rather than milliseconds
- ➖ Requires the poller to be genuinely reliable, since it is now the only recovery path

## Why we chose what we chose

The asymmetry is overwhelming. A retry saves a few seconds when the network hiccups. A duplicate
ticket costs real money, cannot be automatically undone, and lands on a customer who did nothing
wrong.

Recovering slowly is a minor inconvenience. Recovering *wrongly* is a support incident, a refund
we may not be able to process, and a customer who no longer trusts the agent — which is our
actual product.

We already need the polling infrastructure regardless, because `TicketPending` exists and there
are no webhooks. Reusing it for timeout recovery adds no new machinery.

## Consequences

### What this makes easier

- Double-ticketing becomes structurally impossible, not merely unlikely
- One recovery path for every uncertain outcome — timeout, `TicketPending`, and worker crash all
  resolve the same way
- A worker can be killed at any moment during issuance without risk

### What this makes harder

- Recovery is slower. A booking can sit in `IssueOutcomeUnknown` for up to a minute
- The poller is now load-bearing. If it stops, bookings get stuck in limbo, so it needs
  monitoring and alerting from day one
- It is counter-intuitive. A developer adding resilience "correctly" would add a retry here, so
  the code carries a prominent comment pointing at this ADR

### How this is enforced

- The Polly pipeline for `issue` is configured separately from every other supplier call, with
  retry explicitly disabled and a comment linking here
- `supplier_bookings` has `UNIQUE (order_line_id)` — the durable backstop, because a unique
  constraint survives a process dying and a distributed lock does not
- A chaos test fires 20 concurrent issue messages for one order line and asserts exactly one
  supplier call reaches the mock
- A kill test terminates the worker mid-issue and asserts that restart recovery goes through
  `GetBookingStatus` and never re-issues

### What we would need to see to revisit this

Trips Africa adding a genuine idempotency key to the issue endpoint, or a documented, reliable
flight-cancellation endpoint that makes duplicates cheaply reversible.
