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
| 7 | **The return page asked the gateway about any reference it was handed.** `GET /api/v1/public/checkout/{reference}?payment=…` put that reference to Paystack before anything checked it was a payment of ours, of this booking, or from the browser that bought it. Anyone could make us call the gateway for nothing. The public storefront routes named no rate-limit policy either, so the site and its catalog fell back to the default — fifteen times looser than the `Storefront` policy the cart, checkout and CRM routes carry. ([#173](https://github.com/innovateavitech/trips-agent/issues/173), items 1 and 3) | Low | The gateway is asked only about a payment of the order on the page, in the agency the host resolved to, from the browser that bought it, and only while that payment is still waiting for an answer. `/api/v1/public/storefront/*` names the `Storefront` policy. `StorefrontAdversarialTests`, `RateLimitingTests` |
| 8 | **A traveller's document link never expired, and outlived the agency.** The invoice and voucher links on the manage-my-booking page were permanent signatures — no deadline, and nothing asking after the agency's standing — so one in an old email went on working after the traveller's mailbox had changed hands and after the agency was closed, beside a booking link that already stopped. ([#174](https://github.com/innovateavitech/trips-agent/issues/174)) | Low | The link carries a signed deadline and takes the expiry of the booking link that revealed it, so the two lapse together; the download asks `AgencyAccess.CanServeExistingTravellers`, so a suspended agency's travellers keep their documents and a terminated agency's links stop. Links minted before this carry no deadline and are refused: they only ever appeared on that page, which mints a fresh one on every visit. `DocumentLinksTests`, `DocumentEndpointTests` |
| 9 | **Dispute evidence stored whatever file ids it was given.** `SubmitEvidenceAsync` wrote the `AssetIds` out of the request onto the dispute without asking whether they were the agency's own files. Nothing reads them yet — evidence files are not forwarded to Paystack in the MVP — so the only effect was a row pointing at a stranger's file; it stops being harmless the moment something renders or uploads them. ([#175](https://github.com/innovateavitech/trips-agent/issues/175), item 1) | Low | The ids are counted against the agency's own assets behind the tenant filter, before the gateway is told anything, so a refusal spends neither the deadline nor a submission. A list with one id that is not ours is refused whole. `CrossTenantAccessTests` |
| 10 | **The allowance release function trusted its caller for the amount.** `payments.release_sub_agent_allowance` is `SECURITY DEFINER` — the one way a sub-agent's own session writes the allowance row its principal owns — and it took any amount, clamping the result at zero with `GREATEST`. Reserving was already conditional on the limit; releasing was conditional on nothing, so a release for more than was held succeeded quietly, and a double release took whatever other bookings had reserved with it. ([#175](https://github.com/innovateavitech/trips-agent/issues/175), item 3) | Low | Releasing is conditional too: `WHERE spent_minor >= p_amount`. More than that writes nothing and returns false, failing towards counting the cap rather than freeing it. The migration restates the definer rights, the search path and the grant, and the tests read all three back. `SubAgentAllowanceTests` |

## Opened as issues, not fixed here

Each is `module:security` with a severity label, one issue per finding, as the plan asks.

| | Finding | Severity |
|---|---|---|
| [#170](https://github.com/innovateavitech/trips-agent/issues/170) | An invitation can make a *verified* account for an address nobody proved, and that address can then never register for itself | Medium |
| [#171](https://github.com/innovateavitech/trips-agent/issues/171) | A suspended agency still takes trip requests and quote acceptances — the CRM resolves its host its own way and did not get decision 14's rule | Medium |
| [#172](https://github.com/innovateavitech/trips-agent/issues/172) | Any agency user can replace the agency's KYB submission; there is no agency-side permission that fits yet | Low |
| [#173](https://github.com/innovateavitech/trips-agent/issues/173) | Anonymous routes that cost CPU or a gateway call are unthrottled — **all that is left is the invitation-accept route**, which hashes a password with Argon2id before it looks the token up. The other two are fixed above | Low |
| [#175](https://github.com/innovateavitech/trips-agent/issues/175) | Three loose ends — **all that is left is quote tokens stored in plain text**. The evidence assets and the allowance release function are fixed above | Low |

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
