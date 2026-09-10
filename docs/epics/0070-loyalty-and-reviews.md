# Epic #70 — Loyalty programme and reviews

**Epic:** [#70](https://github.com/innovateavitech/trips-agent/issues/70) ·
**Module:** Loyalty & Reviews · **Milestone:** M3 — Network, monetisation & back-office
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Points earn and redeem; verified-purchase reviews with agent moderation and platform override.
> BLOCKED on requirements — see open questions 24. Model the entitlement flag now, build the
> feature once specified.

Twelve issues below cover both halves. Four are loyalty behaviour, five are reviews, one is the
schema underneath both, one is the entitlement flag the epic explicitly asks for now, and one is
the agent-console UI.

The epic body is unusually short because there is nothing to summarise:
[open question 24](../ARCHITECTURE_AND_DELIVERY_PLAN.md) records that loyalty and reviews appear
in FRD §1.2 scope with **no use case, no business rules and no UI**. Everything below is either
structurally determined by the rest of the platform, or explicitly marked as an assumption to be
confirmed.

---

## The two sentences that shape this whole epic

**First — the programme belongs to the agent, not to us.**
[Hard rule #4](../../CLAUDE.md#4-nothing-traveller-facing-may-reference-trips): nothing
traveller-facing may reference Trips. A traveller earning points is a traveller earning *the
agent's* points, displayed in the agent's colours on the agent's domain. There is no
platform-wide Trips loyalty scheme, and there cannot be one without breaking the product's core
promise. The schema already says so: `loyalty_accounts` hangs off `customers`, which is
`UNIQUE (agency_id, email)` ([§2.10](../ARCHITECTURE_AND_DELIVERY_PLAN.md)). The same human
booking through two agencies has two unrelated balances, and neither agency may learn about the
other.

The same applies to reviews. A review is about the agent's product, shown on the agent's site,
moderated by the agent. Our override exists so a fraudulent agency cannot curate its own
reputation — but it must be **invisible**: a review that Trips removes has to look, to the
traveller, exactly like a review the agent removed.

**Second — points are a promise, not money.**
Points are not currency and must never enter the double-entry ledger
([§2.9](../ARCHITECTURE_AND_DELIVERY_PLAN.md)), which balances in naira and is asserted nightly
by job 7. But a *redeemed* point becomes a discount on a real sell price, and that money has to
come from somewhere. It comes out of the agent's markup — the same answer
[open question 4](../ARCHITECTURE_AND_DELIVERY_PLAN.md) gives for the platform fee — which means
redemption collides directly with
[hard rule #5](../../CLAUDE.md#5-prices-are-frozen-at-purchase-never-recalculated): prices are
frozen at purchase. That collision is finding 1 below, and it is the one thing in this document
that needs attention **before M1 finishes**, not in M3.

---

## Read this before picking anything up

**This epic is blocked, and it is blocked more deeply than the label suggests.** The `blocked`
and `needs-decision` labels point at open question 24, but that question cannot be answered on
its own:

[Open question 21](../ARCHITECTURE_AND_DELIVERY_PLAN.md) asks whether storefront travellers get
accounts at all, and **recommends they do not** — guest checkout only, with a magic-link "manage
my booking" page. A points programme with no account is close to unbuildable: there is nowhere
for a traveller to see a balance, and nothing to authenticate against at redemption. So question
24 is downstream of question 21, and answering 24 first will produce a specification that the
platform cannot host.

Reviews survive that answer and loyalty does not, which is why they split cleanly below. A review
invitation is a single-use token in an email — the same mechanism as the magic link — so `L7`
through `L12` work under either answer to question 21.

**What is buildable today, once M1 and M2 land:**

- `L1` — the schema. Table shape is determined by the plan; the *rules* are configuration, and
  configuration values are data, not migrations.
- `L2` — the `loyalty_program` entitlement flag. This is the piece the epic asks for by name.
- `L7`–`L12` — reviews, on assumed-standard mechanics, with one policy question flagged in
  finding 7.

**What is genuinely blocked:** `L3`, `L4`, `L5` and `L6`. Not on engineering — on what the
programme *is*. Earn rate, what earns (spend? bookings? margin?), point value, expiry period,
minimum redemption and who funds the discount are all commercial answers, and guessing them
wrong means either giving away an agent's margin or shipping a programme nobody uses.

---

## The split

`L1`–`L12` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| L1 | Loyalty and reviews schema | ~2 days | #8, #10, #11, #21, #41 |
| L2 | The `loyalty_program` entitlement and the module gate | ~1 day | #64 |
| L3 | Loyalty programme configuration 🚫 | ~2 days | L1, L2 — **blocked** |
| L4 | Points earn on fulfilment, with reversal claw-back ⚠️ 🚫 | ~2 days | L1, L3, #42, #43 — **blocked** |
| L5 | Points redemption as a frozen order-line discount ⚠️ 🚫 | ~3 days | L1, L3, #29, #41, #61 — **blocked** |
| L6 | `LoyaltyPointsExpiryJob` 🚫 | ~1 day | L1, L3, #31, #45 — **blocked** |
| L7 | Review invitations and `ReviewInvitationJob` | ~2 days | L1, #41, #45 |
| L8 | Public review submission by single-use token | ~2 days | L7, #60 |
| L9 | Agent moderation queue | ~2 days | L8, #21 |
| L10 | Platform moderation override ⚠️ | ~1 day | L9, #21, #66 |
| L11 | Rating aggregates and storefront display | ~2 days | L9, #60, #67 |
| L12 | Agent-console screens — loyalty config, points, moderation | ~3 days | L3, L4, L9, #48 |

All twelve carry `module:loyalty-reviews` and the `M3` milestone. `L3`–`L6` additionally carry
`blocked` + `needs-decision`. `L4`, `L5` and `L10` touch tenancy or money invariants and should
be reviewed with the care [rules 2, 3 and 5](../../CLAUDE.md#hard-rules--do-not-break-these) ask
for.

**Order:** `L1` and `L2` first — `L2` is the epic's explicit instruction and is an afternoon's
work in the right place. Then the whole review track, `L7` → `L8` → `L9` → `L10` → `L11`, because
it is unblocked and delivers something on its own. Loyalty waits for question 24; when it
arrives, `L3` → `L4` → `L6`, then `L5` last of the four because it is the one that touches money.
`L12` last of all.

Do **not** reorder `L9` before `L8`. A moderation queue built before there is anything to
moderate gets its test data hand-inserted, and hand-inserted rows are the ones that skip the
verified-purchase check the whole feature rests on.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### L1 · `Loyalty & Reviews: Loyalty and reviews schema`

#### What
The four tables from [§2.15](../ARCHITECTURE_AND_DELIVERY_PLAN.md) —
`loyalty_programs`, `loyalty_accounts`, `loyalty_transactions`, `reviews` — as one EF Core
migration, with the tenant filter and no behaviour.

#### Why the schema is not blocked when the feature is
Question 24 governs *what the rules are*, not *where they live*. An earn rate of 1 point per ₦100
and an earn rate of 5 points per booking are the same column with a different value in it. What
would be expensive to get wrong is the **shape**: an append-only transaction log with a derived
balance, rather than a mutable `points_balance` column that some future code path increments
twice. That shape is the same shape as the wallet ledger
([#22](https://github.com/innovateavitech/trips-agent/issues/22)) and for the same reason — a
balance you cannot reconstruct is a balance you cannot defend when a traveller disputes it.

#### Acceptance criteria
- [ ] `loyalty_programs` — one per agency (`UNIQUE (agency_id)`), `status (draft|active|paused)`,
      and the rule columns `L3` configures. Money-shaped values in **minor units**:
      `point_value_minor` is what one point is worth in kobo, never a decimal
- [ ] `loyalty_accounts` — `UNIQUE (agency_id, customer_id)`, `points_balance` as a cached
      projection plus `version` for optimistic concurrency, mirroring `wallets`
- [ ] `loyalty_transactions` — append-only, `type (earn|redeem|expire|adjust)`, signed
      `points_delta` **integer** (points are counted, not measured), `balance_after`,
      `reference_type/id`, `occurred_at`, `expires_at` on earn rows, and `reason` on `adjust`
- [ ] A trigger or `UPDATE`/`DELETE` denial on `loyalty_transactions` — the same protection
      `order_lines` gets in [#29](https://github.com/innovateavitech/trips-agent/issues/29)
- [ ] `reviews` — `agency_id`, `customer_id`, `order_line_id`, polymorphic
      `subject_type (agency|product|departure)` + `subject_id`, `rating` (CHECK 1–5),
      `title`, `body`, `status (pending|approved|rejected)`, `moderated_by`, `moderated_at`,
      `moderation_reason`, `published_at`
- [ ] `reviews` has `UNIQUE (order_line_id)` — one purchase, one review. This is the
      verified-purchase rule expressed as a constraint rather than as a check somebody remembers
      to write
- [ ] Every table has `agency_id` and a global query filter
      ([#11](https://github.com/innovateavitech/trips-agent/issues/11)); an architecture or
      integration test proves it
- [ ] Indexes: `(agency_id, customer_id)` on accounts, `(agency_id, account_id, occurred_at)` on
      transactions, `(agency_id, subject_type, subject_id, status)` on reviews — the last one
      **is** the storefront's query
- [ ] Migration is additive and reversible; no data backfill

#### Notes
Nothing here writes to `ledger_entries`. See finding 3 — points in the money ledger would break
the nightly integrity assertion, which is a P1 alarm.

**Depends on:** #8, #10, #11, #21, #41

---

### L2 · `Loyalty & Reviews: The loyalty_program entitlement and the module gate`

#### What
The `loyalty_program` boolean entitlement — already named in the plan's `entitlements` enum
([§2.3](../ARCHITECTURE_AND_DELIVERY_PLAN.md)) — seeded, wired into the entitlement middleware,
and enforced on every loyalty endpoint and on the storefront's points display.

#### Why now, when the feature is blocked
This is the epic's own instruction: *"Model the entitlement flag now, build the feature once
specified."* The reason is commercial. `loyalty_program` is a tier differentiator, so tier
definitions, pricing pages and the tier builder in
[#64](https://github.com/innovateavitech/trips-agent/issues/64) all need it to exist. If it
arrives with the feature in late M3, every tier published before then is missing an entitlement
and has to be edited — and `tier_change_log` records that edit as a change to a live tier, which
is not what happened.

#### Acceptance criteria
- [ ] `loyalty_program` seeded into `entitlements` with `value_type = bool`
- [ ] Assigned a value in every seeded tier — explicitly `false` on the tiers that do not get it,
      never absent. An absent entitlement and a `false` entitlement must not be the same code
      path
- [ ] Enforced by the entitlement middleware, returning the standard upgrade-required response,
      not a 404 and not a 500
- [ ] Enforced on the **storefront** too, not only the console. An agency that downgrades must
      stop showing a points balance to travellers
- [ ] Integration test: an agency without the entitlement gets the upgrade response on a loyalty
      endpoint; one with it passes through
- [ ] The downgrade case is written down, not implemented — see finding 6

#### Notes
This issue may belong to [#64](https://github.com/innovateavitech/trips-agent/issues/64) rather
than here; it depends on which breakdown lands first. Proposed here because #70 asks for it by
name and #64 has not been broken down. Whoever creates the issues should put it in one place, not
both.

**Depends on:** #64

---

### L3 · `Loyalty & Reviews: Loyalty programme configuration` 🚫

#### What
The agent-authored rules of their own programme: what earns, at what rate, what a point is worth,
whether points expire, and the minimum redemption.

#### Why it is blocked
Every one of those is a commercial answer and none of them is in the FRD. The defaults below are
the *assumed-standard mechanics* question 24 mentions, written down so the conversation with the
client is a review rather than a blank page:

| Rule | Assumed default | Why the assumption is not safe to just ship |
|---|---|---|
| Earn basis | Per ₦100 of **gross** paid | Earning on gross means points scale with the net rate we charge, not with the agent's own margin. Earning on markup would tie the reward to what the agent actually makes |
| Earn rate | 1 point per ₦100 | Arbitrary |
| Point value | ₦1 per point (1% back) | Sets the whole cost of the programme |
| Expiry | 12 months from earn | Whether points expire at all is a consumer-protection question in NG, not a preference |
| Minimum redemption | 500 points | Arbitrary |
| Rounding | Down, always | Rounding up on every line leaks points at scale |

#### Acceptance criteria
- [ ] One programme per agency, `draft → active → paused`, editable only by a user with the
      loyalty permission
- [ ] Rule changes are **versioned, not overwritten** — a points balance earned under last
      month's rate must stay explicable. Same principle as `tier_prices`
- [ ] Activating a programme requires the `loyalty_program` entitlement (`L2`)
- [ ] `point_value_minor` is a `bigint` in kobo; the earn rate is an integer ratio, not a float
- [ ] Pausing stops earn and redeem but never voids an existing balance — see finding 6
- [ ] Unit tests for the rounding rule at the boundaries, including a ₦99 line and a ₦0 line

#### Notes
Blocked on [open question 24](../ARCHITECTURE_AND_DELIVERY_PLAN.md). The table above is the
proposed answer to take to the client.

**Depends on:** L1, L2

---

### L4 · `Loyalty & Reviews: Points earn on fulfilment, with reversal claw-back` ⚠️ 🚫

#### What
The consumer that awards points when an order line is actually fulfilled, and takes them back
when that fulfilment is undone.

#### Why earn is on fulfilment and not on payment
A paid order is not a delivered order. On the supplier path a payment can be captured and the
ticket still fail to issue, at which point
[#43](https://github.com/innovateavitech/trips-agent/issues/43) reverses the payment. Points
awarded at capture would survive that reversal, and a traveller who was refunded in full would
keep the points — which is free money, granted by us, on the agent's account, with no way to
detect it except by hand.

So: earn on `fulfilment_status = confirmed`, and subscribe to the reversal, cancellation and
refund events to post a compensating `adjust`.

#### Acceptance criteria
- [ ] Consumes the order-line fulfilment event, not the payment event
- [ ] **Idempotent by `(order_line_id, type)`** — the outbox is at-least-once
      ([#30](https://github.com/innovateavitech/trips-agent/issues/30)), so this consumer will
      see the same line twice and must award once
- [ ] Points computed from the programme rule version in force at order placement, not the
      version in force when the consumer runs
- [ ] Refund, cancellation and reversal post a negative `adjust` referencing the original earn
      row, with a `reason`
- [ ] Claw-back may drive a balance negative rather than clamping at zero — clamping hides the
      fact that points were already spent, and the negative balance is the true position. Cover
      it in a test
- [ ] Partial refund claws back pro-rata, rounded the same direction as earn
- [ ] No earn at all when the agency has no active programme or has lost the entitlement
- [ ] `loyalty_accounts.points_balance` is updated in the same transaction as the transaction
      row, under the `version` check
- [ ] Integration test: earn → refund → balance returns to its pre-earn value

#### Notes
Blocked on `L3`'s rules. The *mechanism* above is not blocked and is the part worth reviewing
early.

**Depends on:** L1, L3, #42, #43

---

### L5 · `Loyalty & Reviews: Points redemption as a frozen order-line discount` ⚠️ 🚫

#### What
Redeeming points at checkout: the traveller applies a balance, the sell price drops, and the
discount is snapshotted onto the order line and never recomputed.

#### Why this is the hardest issue in the epic
Three hard rules meet here.

**Rule 5 — prices are frozen at purchase.** The discount cannot be a live calculation that reads
today's point value. It is a snapshot, taken at placement, protected by the same trigger as the
rest of the line's economics.

**Rule 2 — money is minor units.** A points discount is naira, in kobo, as a `bigint`. The points
themselves are an integer count. Two different units in one calculation, converted exactly once,
by the programme's `point_value_minor`.

**Where the money comes from.** The net rate is owed to Trips whatever the traveller pays, so the
discount can only come out of the agent's markup. That gives a hard invariant:

> The loyalty discount on an order line may never exceed that line's `markup_amount_minor`.

Without it an agent's own loyalty programme sells below cost and the shortfall lands on us.

#### Acceptance criteria
- [ ] `order_lines` carries `loyalty_discount_minor` and `loyalty_points_redeemed` — **a new
      column, not a reduction of `markup_amount_minor`** (see finding 1). Margin reporting has to
      distinguish "sold at a lower markup" from "gave points away"; mutating the markup makes
      `markup_rule_id` describe a rule that was not applied
- [ ] `gross_amount_minor` accounts for the discount, and the order-level totals still sum from
      the lines
- [ ] Discount is capped at `markup_amount_minor` per line, enforced in the domain and by a CHECK
- [ ] Redemption reserves points before the payment attempt and releases them if checkout fails —
      the `wallet_holds` pattern. Two concurrent checkouts on one account must not both spend the
      same points
- [ ] Redeem is refused when the balance is insufficient, below the programme minimum, or the
      programme is paused
- [ ] Refunding a line with a redemption **returns the points** and refunds only the money
      actually paid — never the pre-discount gross
- [ ] Post-placement the snapshot is immutable; a test asserts the trigger rejects the update
- [ ] Traveller-facing copy names the agent's programme, never Trips (rule 4)

#### Notes
Blocked on question 24 *and* on question 21 — without an account there is nothing to authenticate
a redemption against. See finding 2. Also read
[ADR-0003](../adr/0003-never-retry-ticket-issuance.md) before touching the checkout path.

**Depends on:** L1, L3, #29, #41, #61

---

### L6 · `Loyalty & Reviews: LoyaltyPointsExpiryJob` 🚫

#### What
The daily job that expires points past `expires_at`, warns before it does, and is job **31** in
[§3](../ARCHITECTURE_AND_DELIVERY_PLAN.md) — which currently lists thirty. See finding 4.

#### Why a warning matters more than the expiry
Silently deleting something a traveller earned is the fastest way to turn a retention feature into
a complaint, and the complaint arrives at the agent, who cannot explain it either.

#### Acceptance criteria
- [ ] Hangfire recurring job, daily, expiring on FIFO order of `expires_at`
- [ ] Writes `type = expire` rows — never mutates or deletes an earn row
- [ ] Notification at T-30 days via [#45](https://github.com/innovateavitech/trips-agent/issues/45),
      in the agent's branding
- [ ] Idempotent: a second run the same day expires nothing twice
- [ ] Skips agencies whose programme is paused or whose entitlement is gone (finding 6)
- [ ] Batched and tenant-safe — it runs across every agency, so it is one of the few places
      running outside a single tenant context. It must still write `agency_id` correctly on every
      row, and it must not use `.IgnoreQueryFilters()` to read balances
      ([rule 3](../../CLAUDE.md#3-never-bypass-the-tenant-filter))
- [ ] Integration test with a clock abstraction, not `DateTime.UtcNow`

#### Notes
Blocked on whether points expire at all (`L3`). If the answer is no, this issue is closed, not
built.

**Depends on:** L1, L3, #31, #45

---

### L7 · `Loyalty & Reviews: Review invitations and ReviewInvitationJob`

#### What
The job that invites a traveller to review — after they have travelled, not after they have paid —
by emailing a single-use token. Job **32**; see finding 4.

#### Why after travel, and why that is harder than it sounds
A review written the moment a card clears reviews the checkout, not the trip. So the invitation
has to fire after travel ends — and **nothing in the schema records when travel ends.**
`departures` have dates, but a supplier flight line's journey dates live inside a snapshot blob,
and a visa has no travel date at all. This issue needs a queryable travel-end date on the order
line. That is finding 5, and it is the second thing in this document that touches M1.

#### Acceptance criteria
- [ ] Daily job selecting fulfilled lines whose travel ended in the configured window
      (default T+2 days)
- [ ] One invitation per order line, ever; `UNIQUE (order_line_id)` on the invitation
- [ ] Token is cryptographically random, single-use, expires (default 30 days), and is stored
      hashed — the same handling as the password-reset token in
      [#17](https://github.com/innovateavitech/trips-agent/issues/17)
- [ ] Email is fully the agent's brand: their name, logo, colours, reply-to. No Trips string
      anywhere in it (rule 4)
- [ ] Nothing is sent for a cancelled, refunded or unresolved line
- [ ] Respects `notification_preferences`; one reminder at most, then silence
- [ ] Skips lines whose review already exists
- [ ] Integration test with a fixed clock covering the window boundaries

#### Notes
Not blocked by question 24 — reviews need no programme. Blocked only on finding 5 having an
answer, which may be as small as one snapshotted date column.

**Depends on:** L1, #41, #45

---

### L8 · `Loyalty & Reviews: Public review submission by single-use token`

#### What
The unauthenticated endpoint the invitation link resolves to: validate the token, resolve the
tenant, accept a rating and a body, store it as `pending`.

#### Why unauthenticated is the right answer here
[Open question 21](../ARCHITECTURE_AND_DELIVERY_PLAN.md) recommends travellers get no accounts.
The token *is* the proof of purchase — it was emailed to the address on an order line that was
fulfilled — so verified-purchase holds without a login. This is exactly why the review track
survives an answer that would block the loyalty track.

#### Acceptance criteria
- [ ] Token resolves to the agency, the customer and the order line; an invalid, expired or spent
      token returns the same generic response as a valid one that has already been used — no
      enumeration
- [ ] The token, not the request, sets the tenant context. A public endpoint must never take
      `agency_id` from the caller
- [ ] Rating 1–5 required; body length-capped; submission stored as `pending`
- [ ] Rate-limited per IP and per token. Until
      [#71](https://github.com/innovateavitech/trips-agent/issues/71)'s limiter exists, ship an
      endpoint-local limit rather than none — this is a public write endpoint on every agent's
      domain
- [ ] Token is spent on success and cannot be replayed
- [ ] Agent is notified that a review is waiting
- [ ] Page is the agent's brand, responsive, and uses design tokens only
      ([rule 7](../../CLAUDE.md#7-never-hard-code-a-colour-font-or-spacing-value))
- [ ] Photo upload is **out of scope** for this issue — see finding 8

#### Notes
Approving a review changes a public page, so `L9`, not this issue, invalidates the storefront
cache.

**Depends on:** L7, #60

---

### L9 · `Loyalty & Reviews: Agent moderation queue`

#### What
The API behind the agent's review queue: list pending, approve, reject with a reason, reply
publicly once.

#### Acceptance criteria
- [ ] Tenant-scoped list of `pending` reviews, oldest first, with the order line's product and
      travel date for context
- [ ] Approve sets `status = approved` and `published_at`, and enqueues the storefront cache
      invalidation (job 15) for the affected host — an approved review that is not visible for
      six hours reads as a bug
- [ ] Reject requires a `moderation_reason`; the reason is internal and never rendered publicly
- [ ] Every decision writes to the platform audit log
      ([#21](https://github.com/innovateavitech/trips-agent/issues/21)) with actor, before and
      after. This is what makes finding 7 detectable at all
- [ ] One agent reply per review, moderated by the same rules, attributed to the agency and not
      to a named staff member
- [ ] A decision is not reversible by the agent once made — reversal goes through `L10`
- [ ] Requires an explicit review-moderation permission, not merely agency membership
- [ ] Tenant-isolation test: agency B cannot see or act on agency A's reviews

#### Notes
The policy question — whether an agent may reject a truthful negative review — is finding 7. This
issue implements the mechanism either way; the mechanism is the same, the guidance in the UI is
not.

**Depends on:** L8, #21

---

### L10 · `Loyalty & Reviews: Platform moderation override` ⚠️

#### What
The back-office ability to overturn an agent's moderation decision, or remove a review outright,
with a full audit trail.

#### Why this exists
An agent who rejects every review below five stars has a perfect rating and a worthless one. The
override is the referee. It is also a cross-tenant power, which is why it lives behind the admin
console and is one of the handful of legitimately audited cross-tenant reads
([rule 3](../../CLAUDE.md#3-never-bypass-the-tenant-filter)).

#### Acceptance criteria
- [ ] Separate `platform_status`, `platform_moderated_by`, `platform_reason` — the agent's
      decision is **not** overwritten. We need to be able to show what the agent did and what we
      did about it
- [ ] Platform status wins over agent status when rendering
- [ ] The traveller-facing result is indistinguishable from an agent removal. No "removed by
      Trips", no Trips branding, nothing that tells a traveller we exist (rule 4)
- [ ] Requires a back-office permission, a mandatory reason, and an audit entry with before/after
      — the same shape the admin console's profile edits use in
      [#66](https://github.com/innovateavitech/trips-agent/issues/66)
- [ ] Any cross-tenant query is explicit, narrow, and commented with why it is one of the
      permitted uses
- [ ] Agent is notified that a decision was overridden, with the reason
- [ ] An anomaly view — agencies rejecting an unusually high share of reviews — as a query or an
      `admin_alerts` type, so the override is triggered by evidence rather than by complaint

#### Notes
Ship the audit entry in the same PR as the override. An unaudited cross-tenant write is the exact
failure [rule 3](../../CLAUDE.md#3-never-bypass-the-tenant-filter) exists to prevent.

**Depends on:** L9, #21, #66

---

### L11 · `Loyalty & Reviews: Rating aggregates and storefront display`

#### What
The average rating and count per subject, maintained as a read model, and the storefront
components that render them.

#### Why an aggregate and not a `COUNT(*)`
A product page is the most-hit page on every storefront and it is served through ISR. Computing an
average over a growing review table on every render is the kind of query that looks free at ten
reviews and is an incident at ten thousand. The plan already has the mechanism —
`agg_*` tables maintained by job 23
([#67](https://github.com/innovateavitech/trips-agent/issues/67)).

#### Acceptance criteria
- [ ] Aggregate per `(agency_id, subject_type, subject_id)`: count, average, and a per-star
      distribution
- [ ] Recomputed on approval, rejection and override — not only on the nightly rebuild
- [ ] Only `approved` reviews with no platform removal are counted
- [ ] Storefront renders rating, count, distribution and paginated reviews with the agent's reply;
      design tokens only, dark and light, responsive to ~400px
- [ ] Reviewer is shown as first name plus last initial — never a full name, never an email
      (finding 9)
- [ ] Zero-review state is a designed empty state, not "0.0 out of 5"
- [ ] Structured data (`AggregateRating`) is emitted for SEO with the **agent** as the publisher
- [ ] Cache invalidation is proven by test: approve a review, the page reflects it

#### Notes
The SEO benefit is a large part of why reviews are worth building at all; getting the schema.org
publisher wrong points it at us instead of the agent.

**Depends on:** L9, #60, #67

---

### L12 · `Loyalty & Reviews: Agent-console screens`

#### What
Three screens: loyalty programme configuration, a customer's points history, and the review
moderation queue.

#### Acceptance criteria
- [ ] Programme configuration form with the `L3` rules, showing the cost implication of the rate
      in plain naira before saving — an agent setting "10 points per ₦100" should see what that
      costs them
- [ ] Points history reads as a statement: date, reason, delta, running balance, with the order it
      came from linked. The same shape as the wallet statement in
      [#51](https://github.com/innovateavitech/trips-agent/issues/51), because it is the same idea
- [ ] Moderation queue with the review, the purchase context, approve, reject-with-reason and
      reply, plus an optimistic update and a visible failure state
- [ ] Loyalty screens are hidden — not broken — when the entitlement is absent, with a path to
      upgrade
- [ ] Components from `packages/ui`, tokens only, no raw colours; `pnpm check:design` passes
- [ ] Loading, empty and error states for all three, using the shared primitives
- [ ] Keyboard-navigable moderation actions and correct focus handling after a decision removes a
      row from the list

#### Notes
Last. Building the queue UI before `L9`'s audit trail exists produces a screen that works and a
history that does not.

**Depends on:** L3, L4, L9, #48

---

## What this breakdown found

Nine things. Two of them need attention before M1 finishes, which is the reason to review this
breakdown now rather than at the start of M3.

### 1. `order_lines` has nowhere to put a discount, and #29 is unstarted

`order_lines` snapshots `net_amount_minor`, `markup_amount_minor`, `tax_amount_minor`,
`platform_fee_minor` and `gross_amount_minor`
([§2.8](../ARCHITECTURE_AND_DELIVERY_PLAN.md)). There is **no discount component of any kind** —
not for loyalty, not for a promo code, not for an agent's goodwill gesture. And
[#29](https://github.com/innovateavitech/trips-agent/issues/29) puts a trigger on the table that
blocks updates after placement, which is correct and which also means the column cannot be added
to existing rows later with any real value in it.

`L5` needs `loyalty_discount_minor` and `loyalty_points_redeemed`. The alternative — quietly
reducing `markup_amount_minor` — breaks margin reporting, because a line sold at a lower markup
and a line with points redeemed against it are commercially different events and would become
indistinguishable.

**Suggested:** comment on [#29](https://github.com/innovateavitech/trips-agent/issues/29) now,
while it is unstarted, proposing a general `discount_amount_minor` + `discount_reason` on the
line rather than a loyalty-specific pair. Discounts will arrive from somewhere regardless — promo
codes are already in `tier_prices` vocabulary — and adding the column in M1 costs nothing, while
adding it in M3 means a migration against a trigger-protected table full of frozen prices.

### 2. Question 24 cannot be answered before question 21

[Question 21](../ARCHITECTURE_AND_DELIVERY_PLAN.md) recommends storefront travellers get **no
accounts** — guest checkout with a magic-link booking page. A points programme needs somewhere to
show a balance and something to authenticate a redemption against.

The options, in the order they should be put to the client:

1. **Magic-link account, extended.** The "manage my booking" page becomes "manage my bookings and
   points", reached by emailed link. No password, no account table beyond `customers`. Honours
   question 21's recommendation and makes loyalty buildable. **Recommended.**
2. **Real customer accounts.** Reverses question 21 and adds registration, password reset,
   lockout and NDPA obligations for a second class of user, per tenant.
3. **Agent-applied points only.** The agent redeems on the traveller's behalf in the console.
   Cheapest, and closest to how a Nigerian travel agency already works over WhatsApp — but the
   traveller never sees a balance, which is most of what makes a loyalty programme work.

Ask 21 and 24 together, in that order. Answering 24 alone produces a specification the platform
cannot host.

### 3. Points must stay out of the money ledger — but redemption has a money leg

`ledger_entries` is double-entry naira and job 7 asserts debits = credits per group nightly, with
a failure declared a P1. Points are not naira and do not balance against anything, so a points
row in that ledger would fail the assertion and page someone.

`loyalty_transactions` is therefore a **separate append-only log** with the same discipline and
none of the double-entry. That is `L1`.

The money leg is real at redemption, though: the traveller pays less, and the agent's margin
absorbs it. That is recorded on the order line (`L5`) and flows through to margin reporting the
same way any other markup movement does — no new ledger account required.

**What is not decided:** whether an agent's outstanding points are recognised as a **liability**.
Under IFRS 15 a loyalty programme is deferred revenue; under "it's a marketing discount" it is
nothing until redeemed. `ledger_accounts.account_type` has no `loyalty_liability` today. The
payouts breakdown is already proposing two additions to that enum
([0069](0069-payouts-disputes-reconciliation.md)), so if recognition-on-earn is wanted, this is
the moment to add a third — while
[#22](https://github.com/innovateavitech/trips-agent/issues/22) is unstarted.

**Recommended:** no ledger recognition on earn for MVP; report outstanding points × point value
as a **memo figure** in analytics, labelled as an estimate and never as a balance. Revisit if
finance says otherwise, and revisit *before* #22 ships.

### 4. Two jobs are missing from the plan's thirty

[§3](../ARCHITECTURE_AND_DELIVERY_PLAN.md) itemises thirty background jobs. Neither of these is
among them, and both are required by tables the plan itself defines:

| Proposed | Cadence | Why it must exist |
|---|---|---|
| 31 · `LoyaltyPointsExpiryJob` | Daily | `loyalty_transactions.type` includes `expire`. Nothing produces that row |
| 32 · `ReviewInvitationJob` | Daily | A verified-purchase review needs an invitation, and no job sends one |

Worth adding to the plan's table when this breakdown merges, so the job list stays the complete
inventory it is used as.

### 5. Nothing records when travel ends

`L7` invites a review after the trip. To find those lines it needs a travel-end date on the order
line, and there is not one:

- Group departures have dates on `departures` — reachable, one join away
- Supplier flight and bus lines carry journey dates inside the snapshot, not as a column — not
  queryable without unpacking JSON per row
- Visas have no travel date at all, only an issue date

**Suggested:** a snapshotted `travel_ends_at` (nullable) on `order_lines`, set at placement from
whatever the item type knows. Raise it on
[#41](https://github.com/innovateavitech/trips-agent/issues/41) alongside finding 1 — both are
one column on the same table, and both are cheap in M1 and awkward in M3.

It is also worth noting how much else wants this column: post-trip nudges, "your trip is
tomorrow" reminders, repeat-booking analytics and the CRM's follow-up tasks all key off travel
having happened. Reviews are just the first to need it.

### 6. Downgrade below the entitlement, with points outstanding

[Question 15](../ARCHITECTURE_AND_DELIVERY_PLAN.md) covers entitlements in use at downgrade and
recommends grandfathering existing usage with a 30-day notice. That works for a custom domain. It
does not work here, because the thing being grandfathered is **a promise made to somebody else's
customer.**

An agency that drops below `loyalty_program` with balances outstanding has travellers holding
points the platform will no longer let them spend. Silently voiding them makes us the party that
broke an agent's promise to their customer.

**Recommended:** on downgrade, stop **earn** immediately and keep **redeem** open for a wind-down
period, with the agent notified of their outstanding liability in naira. Points are then spent
down rather than confiscated. The same reasoning as
[question 14](../ARCHITECTURE_AND_DELIVERY_PLAN.md) (suspended agents with live forward
bookings) — an agent's commercial relationship with us ending must not strand their travellers.

Not implemented in `L2`; written down there so it is a decision and not an oversight.

### 7. There is no moderation policy, and the override implies one exists

The epic gives the agent moderation and the platform an override, and says nothing about what
either may legitimately do. The mechanism is easy; the policy is not, and the policy is a client
answer:

- May an agent reject a review that is accurate but negative?
- If yes, every rating on every storefront is marketing copy, and `L11`'s aggregate is worthless.
- If no, on what grounds may they reject at all — abuse, irrelevance, personal data, defamation?
- What triggers our override: a traveller complaint route that does not exist yet, or the
  rejection-rate anomaly in `L10`?

`L9` and `L10` are buildable without the answer. The **in-product guidance** — what the reject
dialog tells an agent they are allowed to do — is not, and shipping the wrong guidance is worse
than shipping none.

**Recommended:** rejection limited to stated grounds (abuse, off-topic, personal data, evidently
false claim), with an appeal path to us. Needs client sign-off with question 24.

### 8. Review photos are assumed out of scope

Travellers photograph trips, and a tour listing with customer photos sells better than one
without. But photos mean `assets`, virus scanning and EXIF stripping (job 22,
[#18](https://github.com/innovateavitech/trips-agent/issues/18)), moderation of image content
rather than text, storage cost per tenant, and a takedown path.

`L8` excludes them deliberately. If they are wanted, that is a thirteenth issue of ~3 days, not a
checkbox on `L8` — and it should be scoped after the text review flow is live.

### 9. Open questions this epic touches

Per [CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), these are flagged rather than
guessed at:

| # | Question | Effect here |
|---|---|---|
| 4 | Platform fee on the price or off the margin | The precedent for who funds a points discount. `L5` follows it: off the agent's margin |
| 14 | Suspended agents with live forward bookings | Same shape as finding 6 — outstanding points when a commercial relationship ends |
| 15 | Downgrade with entitlements in use | Finding 6. Grandfathering does not work for a promise made to a third party |
| 21 | Do storefront travellers get accounts? | **Gates `L5` outright** and must be answered before 24. Finding 2 |
| 24 | Loyalty and reviews have no requirements | **Blocks `L3`–`L6`.** The whole reason for the epic's `blocked` label |
| 26 | NDPA erasure vs. financial retention | A published review holds a reviewer's name on a public page indefinitely. Erasure must anonymise the review while keeping the rating, so the aggregate does not move. Same conflict as the security epic's `S5` |

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#70](https://github.com/innovateavitech/trips-agent/issues/70)
3. **Comment on [#41](https://github.com/innovateavitech/trips-agent/issues/41) and
   [#29](https://github.com/innovateavitech/trips-agent/issues/29) now**, while both are
   unstarted — a general `discount_amount_minor` + `discount_reason` (finding 1) and a
   snapshotted `travel_ends_at` (finding 5). Both are one column in M1 and a migration against
   trigger-protected frozen prices in M3
4. Take questions **21 and 24 to the client together, in that order** (finding 2), with the
   assumed-mechanics table in `L3` and the moderation grounds in finding 7 as the starting draft
5. Decide points-liability recognition before
   [#22](https://github.com/innovateavitech/trips-agent/issues/22) ships (finding 3), and add
   `loyalty_liability` to `ledger_accounts.account_type` only if finance requires it
6. Add jobs 31 and 32 to [§3 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) (finding 4)
7. Create `L1`, `L2` and `L7`–`L12` with `module:loyalty-reviews` + `M3`; create `L3`–`L6` with
   `blocked` + `needs-decision` as well. Rewrite every `Depends on:` line with real numbers, and
   decide whether `L2` belongs here or in
   [#64](https://github.com/innovateavitech/trips-agent/issues/64)'s breakdown
8. Keep #70 open as the tracking epic
9. `./scripts/generate-backlog.sh`
