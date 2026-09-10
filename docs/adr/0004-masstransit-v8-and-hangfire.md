# ADR-0004: Pin MassTransit to v8, and split cron from events

**Status:** Proposed
**Date:** 2026-09-10
**Deciders:** Platform team (issue #31)

## Context

The platform needs two different kinds of background work, and they are not the same thing:

- **Things that happen because something else happened.** A payment is captured, so the supplier
  must be confirmed; a booking is ticketed, so a voucher must be rendered and emailed. These are
  event-driven, they form long-running flows, and every one of them is on the money path.
- **Things that happen because the clock says so.** Poll the supplier every 30 seconds. Expire
  carts every minute. Audit the ledger nightly.

The delivery plan (§3) already named the tools — MassTransit over RabbitMQ for the first,
Hangfire for the second — but not the versions, and in the eighteen months since the plan was
written the licensing underneath MassTransit changed.

**MassTransit v9 is commercially licensed.** Versions up to and including 8.x are Apache-2.0
(`Copyright 2007-2024 Chris Patterson`). From 9.0 the package is published by Massient, Inc.
under a paid licence at <https://massient.com/license>. This is not a subtlety in the small
print; it is a different licence on a package that sits on the booking path.

We are pre-revenue and pre-launch. We are also building on a supplier API with no webhooks, which
means the polling and saga machinery is not optional garnish — it is how the product works at all.

## Decision

We use **MassTransit 8.5.x**, pinned to the 8 major, for events and sagas; and **Hangfire 1.8.x
with `Hangfire.PostgreSql`** for recurring jobs. Both run in `TripsAgent.Worker`, a process
separate from the API.

The two are not interchangeable and the line between them is:

| Tool | Used for |
|---|---|
| MassTransit | Sagas, domain events, retries, the outbox — everything on the booking/money path |
| Hangfire | Recurring scheduled jobs, and the dashboard |

## Options considered

### Option A — MassTransit 9.x

- ➕ Current. Gets the fixes, the docs and the Stack Overflow answers.
- ➕ Supports .NET 10 as a first-class target.
- ➖ Costs money, on a product that has not yet taken a naira.
- ➖ A licence decision made now, quietly, in a `.csproj`, that binds the company later. Ripping
  MassTransit out once sagas are written is not an afternoon's work.

### Option B — MassTransit 8.5.x (Apache-2.0)

- ➕ Free, and permissively licensed.
- ➕ Ships a `net10.0` target, so this is not a compatibility compromise.
- ➖ Will stop receiving fixes at some point. 8.x is a maintenance line, not the future.
- ➖ Deferring the licence decision, not making it.

### Option C — Raw `RabbitMQ.Client`, no framework

- ➕ No licence question at all.
- ➖ We would write our own retry, backoff, dead-lettering, serialisation, saga persistence and
  outbox. That is a large amount of subtle concurrency code, on the money path, written by a team
  that is new to .NET. Every one of those is a place to lose a booking.

### Option D — Hangfire for everything, no message broker

- ➕ One tool instead of two.
- ➖ Hangfire is a job scheduler, not a bus. It has no sagas, no publish/subscribe and no
  transactional outbox, so the checkout flow would become a hand-rolled state machine in a
  database table.

## Why we chose what we chose

Option B buys time without costing capability. The 8.5 line targets `net10.0`, so nothing is
being held back technically; the only thing we give up is future fixes on a library whose 8.x
line has been stable for years.

The alternative was to commit real money to a licence before the product has a single paying
agent, on behalf of a decision the company has not been asked to make. Pinning to 8.x turns that
into a deliberate choice with an owner and a trigger, instead of a surprise invoice.

Option C was never seriously in play. Rule 6 in CLAUDE.md exists because getting retry semantics
wrong on this supplier issues a second real ticket that we cannot refund. That is exactly the
class of bug a mature messaging library has already found and fixed.

We keep Hangfire alongside rather than folding cron into MassTransit because the two failure
modes are different. A missed cron tick should just run again next tick; a lost domain event is a
customer whose money moved and whose ticket never came. Different guarantees, different tools,
and a dashboard for the half where "did that job run?" is the question people actually ask.

## Consequences

### What this makes easier

- No licence spend during M1, and no licence conversation blocking the first release.
- The transport stays swappable. Everything the application touches goes through `IMessageBus`;
  an architecture test fails the build if `MassTransit` is ever referenced from `Application` or
  `Domain`.
- Hangfire's dashboard gives non-.NET people a way to see whether the pollers are alive.

### What this makes harder

- **8.x will eventually stop receiving security fixes.** When that happens the choice is pay for
  v9, or migrate to another bus. Neither is free, and the second is worse the longer we wait.
- Two tools means two mental models, two sets of retry semantics and two dashboards. New
  developers have to learn which kind of work goes where — which is why the table above exists.
- Hangfire keeps its own tables, in its own `hangfire` schema, created outside `db/migrations/`.
  `scripts/check-migrations.sh` does not know about them. That is expected, and surprising the
  first time you run `\dn` in psql.
- Hangfire.Core pulls `Newtonsoft.Json >= 11.0.1`, and NuGet resolves the lowest version in a
  range. 11.0.1 has a high-severity advisory, so `Directory.Packages.props` pins it forward to
  13.x. Remove that pin when Hangfire raises its floor.

### What we would need to see to revisit this

- A security advisory against MassTransit 8.x with no 8.x patch.
- Revenue that makes the v9 licence a rounding error — at which point upgrading is the boring
  choice and this ADR should be superseded.
- The cloud decision landing on something with a managed bus (SQS, Service Bus) whose own SDK is
  simpler than keeping MassTransit at all.
