# Internal adversarial pass, September 2026

Issue 110 asks for a penetration test. Before paying somebody to find what we already knew, we
attacked the highest-risk surfaces ourselves: every tenant-scoped endpoint group, ids in URLs, the
public storefront and magic links, the webhook signature, margin leaking to sub-agents and
travellers, and privilege escalation between agency and back-office roles.

**This is not a penetration test.** It is our own reading of our own code, and it found eight things
in a day, which is mostly a statement about how much there is to read. The external test and its
retest are still a launch gate — see [PENETRATION_TEST_SCOPE.md](PENETRATION_TEST_SCOPE.md).

---

## Fixed, each with a test

| | Finding | Severity | Fix |
|---|---|---|---|
| 1 | **A booking could be opened by guessing its order number.** `GET /api/v1/public/checkout/{reference}` looked an order up by `OrderNumber` alone and, for a paid one, minted a manage-booking link — the traveller's name, their PNR, their invoice and voucher. Order numbers are gapless per agency, so counting from `ORD-2026-000001` walked the whole shop. | Critical | The answer now goes only to the cart session that became that order. A guess gets the same "we could not find that booking" as a miss, and mints nothing. `StorefrontAdversarialTests` |
| 2 | **A suspended agency went on selling.** Decision 14 takes the shop offline, and only the `/site` page honoured it. The cart, the checkout and the public departures API took orders and payments when called directly with the host name. | High | The rule moved to `StorefrontTenant`, where every storefront request resolves its host. Travellers who already paid still reach their booking, and a terminated agency's links stop. `StorefrontAdversarialTests` |
| 3 | **A closed agency's staff could sign back in.** Terminating an agency — which is also what revoking a sub-agent does — changed one column on the agency and left its people `Active`. Sign-in and refresh only ever checked the user. | High | Both now read the agency's standing through `AgencyAccess.CanSignIn`. Suspension deliberately still signs in: decision 14 says a suspended agency may read and may not sell. `AuthenticationTests` |
| 4 | **A colleague could take your report file.** The report job endpoints asked only for a signed-in caller, so any user in the agency could list and download every colleague's runs — and a finished report can carry net rate and markup. The queued path had a second hole: the worker decided whether to write margin columns from role grants alone, ignoring the permission overrides a principal sets on a sub-agent. | High | Report runs are scoped to the person who asked for them, and the worker subtracts the principal's denials. `ReportingTests` |
| 5 | **A preview was publicly cacheable.** A response built from a preview token still carried `Cache-Control: public`, so a CDN could serve an agency's unpublished draft — pages and prices — to every visitor of that host. | Medium | Previews are `private, no-store`, and `Vary` names the preview header either way. `PublicStorefrontEndpointTests` |
| 6 | **A pending domain claim could hide a verified one.** Only verification is exclusive, so several agencies can hold a pending claim on one hostname; host resolution took whichever row PostgreSQL returned first. An agency that claimed a rival's domain and never proved it could take that rival's storefront offline. | Medium | Verified rows win, then oldest. `SiteDomainDirectory` |
| 7 | **An invitation made a *verified* account for an address nobody had proved.** The console shows the principal the invitation link, so a principal could accept its own invitation for a rival's address and hold an account marked verified that nobody there ever saw — and, one address being one account platform-wide, its owner could then never register it. | Medium | Accepting creates the account **unverified** and sends the address the same six-digit code registration sends, through the same issuer and throttle; it signs in only once that code comes back. The code carries the principal's brand, like the invitation before it (decision 6). `SubAgentNetworkEndToEndTests` |
| 8 | **A suspended agency still took trip requests and quote acceptances.** Finding 2 moved decision 14 into `StorefrontTenant`, but the CRM's public routes resolved their host a second way and so never got the rule: a suspended — or terminated — agency's trip-request widget went on taking leads, and its customers could still accept quotes. | Medium | `StorefrontCrmService` resolves its host through `StorefrontTenant` like every other storefront route, so one place decides. Taking a lead and answering a quote are new business (`CanServeStorefront`); reading a quote already sent is a traveller coming back to something of theirs (`CanServeExistingTravellers`), and that answer carries `canRespond: false`, so the page never offers what the next request would refuse. `CrmEndpointTests` |
| 9 | **The invitation-accept route hashed a password before it looked the token up** (issue 173, item 2). Argon2id is expensive on purpose, so an anonymous caller could spend that CPU by posting any string as a token — and the route named no rate-limit policy, so it fell back to the default per-address one. | Low | The token is looked up first, and only a link that turns out to be real is paid for. The route has a policy of its own, `InvitationAccept`: ten in fifteen minutes per address. `RateLimitingTests`, `SubAgentNetworkEndToEndTests` |
| 10 | **Any agency user could replace the agency's KYB submission.** Every route under `/api/v1/kyb` asked only that the caller was signed in, so a counter agent — who can change nothing else about the agency — could upload a document over the agency's own, or send the whole submission back for review. | Low | A new owner-level permission, `kyb.submit`, guards the group. Of the agency roles only the Owner holds it: a Manager runs the day-to-day business and does not restructure it, which is where `billing.manage` already sits. It is reference data, so `migrate` adds it to every existing database and grants it to the Owner role — no migration of its own. The console offers the screen to an Owner alone. `KybAuthorizationTests` |

## Opened as issues, not fixed here

Each is `module:security` with a severity label, one issue per finding, as the plan asks.

| | Finding | Severity |
|---|---|---|
| [#170](https://github.com/innovateavitech/trips-agent/issues/170) | An invitation can make a *verified* account for an address nobody proved, and that address can then never register for itself | Medium |
| [#173](https://github.com/innovateavitech/trips-agent/issues/173) | Anonymous routes that cost CPU or a gateway call are unthrottled — item 2, the invitation route, is fixed above; items 1 and 3 remain | Low |
| [#174](https://github.com/innovateavitech/trips-agent/issues/174) | A traveller's document link never expires and outlives the agency | Low |
| [#175](https://github.com/innovateavitech/trips-agent/issues/175) | Three loose ends: dispute evidence assets unchecked, quote tokens stored in plain text, the allowance release function trusting the caller | Low |

## What held up

Worth recording, because it says where the design is already doing its job:

- **The tenant filter.** Every `ITenantScoped` entity has it, by convention rather than by hand; no
  entity with an `AgencyId` is missing one; there is no `IgnoreQueryFilters` anywhere in production
  code (the analyser would fail the build); raw SQL appears only in background jobs. Every
  `IPlatformScope` in a request path re-states the agency predicate itself.
- **The Paystack webhook.** Raw body, HMAC-SHA512 compared in fixed time before anything is parsed,
  an empty secret refused, replays deduplicated on the gateway's event id, and the payment verified
  with the gateway afterwards rather than trusted from the payload.
- **Authentication.** 256-bit tokens stored as keyed hashes, fixed-time OTP comparison with a
  five-attempt burn, the same answer for an unknown address as for a wrong password, a reset
  revoking every session. Finding 3 above was the gap, and it was about the *agency*, not the user.
- **Margin.** `margin.view` governs it in one place, the response shape is chosen at the projection
  so net and markup are absent rather than null, and a principal's denial bites on the next
  request. Finding 4 was the one path that went round it — a file, written by a worker with no
  claims to read.
- **The back office.** No agency role holds a `platform.*` permission, there is no API for an
  agency to change its own roles, and payout approval refuses the person who requested it.
- **Catalog, CRM, documents, assets, storefront builder, sub-agent management.** A foreign id
  answers "not found", and each already had a test saying so.

## Where regression tests were added

The point of writing a test for each finding is that a hole found once is a hole that can come
back. Beyond the six above, the groups that had no cross-tenant HTTP test at all — bookings,
payouts, disputes and the sub-agent routes — are covered by
`backend/tests/TripsAgent.IntegrationTests/Tenancy/CrossTenantAccessTests.cs`.
