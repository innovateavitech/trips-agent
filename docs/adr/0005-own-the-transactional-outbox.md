# ADR-0005: Own the outbox table and dispatcher; MassTransit carries the messages

**Status:** Proposed
**Date:** 2026-09-10
**Deciders:** Issue #30; direction confirmed by the project owner

## Context

When a handler saves a change and then publishes an event about it, those are two systems —
PostgreSQL and RabbitMQ — and no transaction spans both. Whichever goes second can fail after the
first has succeeded. On the booking path that means a wallet debited with no ticket flow started,
or a ticket flow started for a payment that rolled back. That is the dual-write problem, and issue
#30 exists to close it.

[ADR-0004](0004-masstransit-v8-and-hangfire.md) lists "the outbox" under MassTransit in its split of
responsibilities, and MassTransit 8 ships an Entity Framework outbox. Issue #30, meanwhile, specifies
our own `outbox_messages` and `inbox_messages` tables and a dispatcher that polls every 1–5 seconds.
This record settles which one "the outbox" means.

## Decision

The outbox and inbox are ours: two tables in the `platform` schema and a dispatcher in the Worker.
MassTransit carries what the dispatcher publishes, through `MassTransitOutboxPublisher`, and keeps
everything else ADR-0004 gives it — transport, consumer retries, dead-lettering and sagas.

Read ADR-0004's "the outbox" row as **delivery** of outbox messages. Storing and dispatching them
is decided here.

## Options considered

### Option A — MassTransit's Entity Framework outbox

- ➕ Maintained by someone else; well tested
- ➕ Delivery, inbox deduplication and table cleanup come as one package
- ➖ Raising domain events from `SaveChanges` gets awkward: MassTransit's outbox needs the
  `DbContext`, and the `DbContext` would need MassTransit's publisher — a circular dependency
- ➖ Ties storage of every pending message to MassTransit, whose v9 is commercially licensed and
  whose transport may change when the cloud is chosen
- ➖ Behaviour lives in MassTransit configuration, which is harder for a new developer to follow
  than a few hundred lines of our own code

### Option B — our own outbox, published through MassTransit (chosen)

- ➕ Matches issue #30 and plan §2.14 exactly: named tables, a 1–5s dispatcher, backlog alerting
- ➕ Domain events are captured in `AppDbContext.SaveChangesAsync` with no circular dependency
- ➕ Swapping transport means one new `IOutboxPublisher`; nothing that stores messages changes
- ➕ Everything is in `Infrastructure/Messaging/`, readable in one sitting
- ➖ We own the edge cases: locking, retry backoff, poison messages, backlog alerting
- ➖ Table cleanup is not built yet — see below

## Why we chose what we chose

The part MassTransit is best at — talking to a broker, retrying consumers, dead-lettering — it still
does. The part we take on is small enough to test directly against real PostgreSQL:
`FOR UPDATE SKIP LOCKED` for several Workers at once, a primary key for deduplication, and an
integration test that kills the process between commit and publish. In return, the thing that makes
the booking path trustworthy does not depend on which transport we end up on.

## How it works

1. **Write.** `AggregateRoot.Raise(event)` records a domain event. `AppDbContext.SaveChangesAsync`
   turns each one into a `platform.outbox_messages` row *in the same transaction* as the change.
   `IOutbox.Enqueue` does the same for messages that do not belong to one aggregate.
2. **Dispatch.** The Worker's `OutboxDispatcherService` wakes every `Outbox:PollInterval` (1–5s),
   claims due rows with `FOR UPDATE SKIP LOCKED`, and hands each to `IOutboxPublisher`. The
   MassTransit implementation publishes it with the row's id as the `MessageId`. A row is marked
   dispatched only once the broker has accepted it. A failed publish backs off exponentially; after
   `Outbox:MaxAttempts` the row is marked `failed` for a person to look at.
3. **Consume.** `IInbox.ProcessOnceAsync` writes `(message_id, consumer)` to
   `platform.inbox_messages` in the same transaction as the consumer's work. A duplicate either finds
   the row or loses the race on the primary key and rolls back.
4. **Monitor.** Every `Outbox:BacklogCheckInterval` the Worker measures the pending count, the failed
   count and the age of the oldest pending message, and logs at Warning or Error past the
   thresholds. The API's `/health` reports the same numbers, as Degraded when they are bad.

Delivery is **at-least-once**. A Worker that dies after publishing but before committing publishes
again on restart; the inbox is what makes that harmless.

`IMessageBus.PublishAsync` still exists and still publishes immediately. It is not transactional, so
it is for messages that follow from nothing being saved.

## Consequences

### What this makes easier

- Any handler publishes reliably by raising a domain event; it never touches the broker
- A stuck message is a row you can `SELECT` in psql, with its last error on it
- Moving to SQS or Service Bus replaces one small class

### What this makes harder

- **Tables grow.** Nothing deletes dispatched outbox rows or old inbox rows yet. The partial indexes
  keep the dispatcher fast regardless, but a retention job is needed before production volumes.
  Inbox rows must be kept at least as long as a message can be redelivered.
- **No ordering.** Retries and parallel Workers reorder messages. Consumers must not assume
  `OrderPlaced` arrives before `OrderPaid`.
- **Side effects outside the database are not deduplicated.** A consumer that sends an email or
  calls the supplier must pass the message id on as an idempotency key.
- **Alerts are logs and a health status.** There is no metrics pipeline or `admin_alerts` table yet.
  When either lands, the backlog check should feed it.

### What we would need to see to revisit this

- The outbox needing features — scheduled delivery, ordered streams per aggregate — that would cost
  more to write than to adopt, or
- A move to a transport whose own outbox is transport-agnostic and free to use.
