# Epic #59 — Custom domains, DNS verification and SSL

**Epic:** [#59](https://github.com/innovateavitech/trips-agent/issues/59) ·
**Module:** Storefront · **Milestone:** M2 — Storefront, catalog & customer commerce
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Free subdomain provisioning; custom domain with TXT/CNAME verification; automatic SSL issuance
> and renewal at T-30 days; host-header tenant resolution cached in Redis; reserved-hostname
> denylist to stop subdomain squatting (open question 20).

Ten issues below cover all five, plus two pieces the plan requires but the epic body does not
mention: the primary-domain and suspension lifecycle (FRD §2.15 RS-3 — suspending an agent must
take their site offline) and the SSL-failure runbook the plan already lists as expected to exist.

---

## Read this before picking anything up

**None of these can start today**, and one of them cannot be finished at all until an
infrastructure decision is made.

This epic sits near the end of a long dependency chain. It needs the tenanted spine from M1 —
[#8](https://github.com/innovateavitech/trips-agent/issues/8) (EF Core and migrations),
[#11](https://github.com/innovateavitech/trips-agent/issues/11) (tenant context and query
filters), [#12](https://github.com/innovateavitech/trips-agent/issues/12) (RLS) — and it needs
the `sites` table, which belongs to [#58](https://github.com/innovateavitech/trips-agent/issues/58),
the website builder epic, which has not itself been broken down. All of those are open.

That is not a reason to delay the breakdown. Two of the findings below change decisions taken
**before** this epic is picked up: `site_domains` needs a deliberate, audited exception to the
tenant filter and to RLS ([finding 1](#1-the-hosttenant-lookup-is-a-fourth-legitimate-tenant-filter-bypass)),
which is a change to how [#11](https://github.com/innovateavitech/trips-agent/issues/11) and
[#12](https://github.com/innovateavitech/trips-agent/issues/12) are written; and the certificate
strategy ([finding 3](#3-icertificateprovisioner-has-no-chosen-implementation-and-the-decision-is-not-ours-to-defer))
is an infrastructure commitment that wants deciding long before the day D6 is picked up.

---

## The split

`D1`–`D10` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| D1 | `site_domains` and `site_domain_checks` schema ⚠️ | ~2 days | #8, #11, #12, #58 |
| D2 | Hostname normalisation and the reserved-hostname denylist | ~2 days | D1 |
| D3 | Free subdomain provisioning at site creation | ~1 day | D1, D2, #58 |
| D4 | Custom domain registration and DNS instructions | ~2 days | D1, D2 |
| D5 | `IDnsResolver` port and the DNS verification job | ~2 days | D4 |
| D6 | `ICertificateProvisioner` port, issuance and T-30 renewal 🚫 | ~2 days | D5 — **needs a decision** |
| D7 | Host-header tenant resolution and the Redis host map ⚠️ | ~2 days | D1, Redis (see gaps) |
| D8 | Primary domain, removal and suspension lifecycle | ~1 day | D3, D4, D7 |
| D9 | Agent console — the Domains screen | ~2 days | D4, D5, D6, #48 |
| D10 | SSL and DNS failure alerting and runbook | ~1 day | D5, D6 |

All ten carry `module:storefront` and the `M2` milestone. D1 and D7 additionally carry the
tenancy-review flag — both touch the tenant boundary. D6 additionally carries `blocked` and
`needs-decision`.

**Order:** D1 → D2 first, because everything else reads them. Then D3 and D4 together (they share
the same write path), then D5. D6 next if the certificate decision has been made by then, D8
alongside it. D9 last of the backend-dependent work, and D10 with or just after D6.

D7 is worth pulling **early** despite sitting mid-table. It is what
[#60](https://github.com/innovateavitech/trips-agent/issues/60) (public storefront rendering)
blocks on, and it needs no DNS and no certificate — a row in `site_domains` and a `Host` header
are enough to build and test the whole thing.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### D1 · `Storefront: site_domains and site_domain_checks schema`

#### What
The two tables behind every hostname the platform serves, their EF Core configuration, and the
migration.

#### Acceptance criteria
- [ ] `site_domains`: `agency_id`, `site_id`, `hostname citext`, `type (subdomain|custom)`,
      `verification_status (pending|verifying|verified|failed|abandoned)`,
      `verification_method (TXT|CNAME)`, `verification_token`,
      `ssl_status (none|pending|issued|failed|expired)`, `ssl_expires_at`, `last_checked_at`,
      `is_primary`, `created_at`
- [ ] `hostname` is **globally** `UNIQUE`, not unique per agency — two agencies claiming the same
      hostname is the one thing this table exists to prevent, and a tenant-scoped uniqueness
      constraint would allow exactly that
- [ ] `citext`, so `Booking.Zara.com` and `booking.zara.com` are the same row (see D2 for why the
      column type is not sufficient on its own)
- [ ] `site_domain_checks`: `site_domain_id`, `checked_at`, `method`, `expected_value`,
      `observed_values text[]`, `outcome (match|mismatch|nxdomain|timeout|servfail)`,
      `resolver_used`, `error_detail`. One row per attempt, never updated
- [ ] `agency_id` on both tables with the standard EF global query filter — **plus** the audited
      bypass described below, which is the whole reason this issue carries a tenancy review
- [ ] Index `(site_id)` and `(verification_status, last_checked_at)` — the second is what D5's job
      sweeps
- [ ] Index `(ssl_status, ssl_expires_at)` — what D6's renewal scan sweeps
- [ ] Index `(site_domain_id, checked_at DESC)` on the checks table, for D9's history panel
- [ ] Architecture and RLS tests still pass

#### The tenant-filter bypass, and why it is legitimate
Resolving a `Host` header to a tenant happens **before there is a tenant** — that lookup is how
the tenant is discovered. It therefore cannot run under the tenant filter, and under
[#12](https://github.com/innovateavitech/trips-agent/issues/12)'s RLS it would return zero rows
on a connection with no `agency_id` set.

[CLAUDE.md rule 3](../../CLAUDE.md#3-never-bypass-the-tenant-filter) says the legitimate bypasses
are all in platform-admin reporting. This is not, so it must be made explicit rather than
discovered by whoever writes D7 and quietly reached for `.IgnoreQueryFilters()`:

- [ ] A single named repository method — `IHostResolver.ResolveAsync(hostname)` or similar — is
      the **only** place in the codebase that reads `site_domains` untenanted
- [ ] It selects `site_id`, `agency_id` and status columns and nothing else. It must not become a
      general-purpose untenanted read of the table
- [ ] It is covered by a test asserting the projection, so widening it later fails the build
- [ ] [#12](https://github.com/innovateavitech/trips-agent/issues/12)'s RLS policy for
      `site_domains` permits this read explicitly — a policy carve-out, a dedicated role, or a
      `SECURITY DEFINER` function. Decide it here and write it down
- [ ] `CLAUDE.md` rule 3 is updated to name this as the known non-admin exception

**Depends on:** #8, #11, #12, #58

---

### D2 · `Storefront: Hostname normalisation and the reserved-hostname denylist`

#### What
A pure domain-layer component that decides whether a string is a hostname this platform will
accept, and what its canonical form is. No database, no network — so it is fast to test and every
rule is visible in one place.

#### Why it is its own issue
Both D3 and D4 write to `site_domains`, and both need exactly the same answer to "is this
allowed". Written twice, they will disagree, and the disagreement will be the bug that lets
`Emirates.tripsagent.com` through the path that did not get the check.

#### Acceptance criteria
- [ ] Normalises to a canonical form: lowercased, trailing dot stripped, Unicode converted to
      IDNA/punycode. `zaratours.com` written in another script and its `xn--` form are one
      hostname, not two — otherwise the global uniqueness constraint in D1 is bypassable
- [ ] Validates against RFC 1123: label length ≤ 63, total length ≤ 253, `[a-z0-9-]`, no leading
      or trailing hyphen per label
- [ ] Rejects hostnames whose labels mix scripts — the standard homograph defence. `аpple.com`
      with a Cyrillic а is not `apple.com`, and a customer cannot tell the difference
- [ ] A reserved-word denylist, seeded and stored as data rather than a constant so it can be
      extended without a deployment. Covers at minimum: infrastructure (`www`, `api`, `admin`,
      `app`, `mail`, `smtp`, `ftp`, `cdn`, `assets`, `static`, `staging`, `dev`, `test`), our own
      brand (`trips`, `tripsagent`, `tripsafrica`, `support`, `help`, `status`, `billing`,
      `account`, `login`, `secure`), RFC 2142 mailbox names (`postmaster`, `hostmaster`,
      `webmaster`, `abuse`, `security`, `noc`), and verification prefixes (`_acme-challenge`,
      `_trips-verify`)
- [ ] Denylist matching runs **after** normalisation, and catches confusable spellings —
      `tr1ps`, `trips-agent` and `tripsagent` are all the same claim
- [ ] Applies to subdomain labels (D3) and to whole custom hostnames (D4), with the denylist
      scoped appropriately to each: `admin` is forbidden as *our* subdomain, but
      `admin.someagency.com` is the agent's own zone and is theirs to use
- [ ] Unit tests are the deliverable here as much as the code — one case per rule, including the
      punycode and mixed-script cases

#### Notes
This is scope item five, and it is the mechanism half of
[open question 20](../ARCHITECTURE_AND_DELIVERY_PLAN.md). The other half — manual review against
a known-brand list — has no owner and no list, and is deferred rather than guessed at. See
[finding 5](#5-open-question-20-is-half-answerable-and-half-blocked).

A denylist is not a trademark check. It stops the obvious and the automated; it does not stop a
determined squatter registering `emiratesairline-ng.tripsagent.com`. That is what the deferred
review queue is for, and it is worth being honest that MVP ships without it.

**Depends on:** D1

---

### D3 · `Storefront: Free subdomain provisioning at site creation`

#### What
Every site gets `<slug>.tripsagent.com` the moment it is created, verified and serving, with no
DNS and no certificate work required of the agent.

#### Why it is separate from D4
A subdomain is a different problem to a custom domain despite sharing a table. We control the
zone, so there is nothing to verify — the row is born `verified`. And it is covered by the
wildcard certificate, so there is nothing to issue. D4's entire machinery of tokens, polling and
ACME does not apply, and folding the two together produces a state machine whose branches are
no-ops half the time.

#### Acceptance criteria
- [ ] On site creation, a subdomain row is created with `type = subdomain`,
      `verification_status = verified`, `ssl_status = issued`, `is_primary = true`
- [ ] The slug is derived from the agency's name, normalised through D2, and passes the denylist
- [ ] Collisions resolve deterministically — a numeric suffix — rather than failing site creation.
      The second `Zara Travel` gets `zara-travel-2`, not an error
- [ ] The agent can change it once, subject to the same rules, with the old hostname held for
      30 days rather than immediately re-issued to someone else
- [ ] Creation is idempotent — a retried site creation does not produce a second subdomain
- [ ] The base domain (`tripsagent.com`) is configuration, added to `.env.example`. Staging and
      production do not share a zone
- [ ] Integration test: creating a site produces a resolvable, primary, verified subdomain row

#### Notes
This assumes a **wildcard DNS record and a wildcard certificate** for `*.tripsagent.com`, which is
what makes provisioning free and instant. That is an infrastructure prerequisite, not application
work, and it is bound up with the certificate decision in
[finding 3](#3-icertificateprovisioner-has-no-chosen-implementation-and-the-decision-is-not-ours-to-defer).
Without a wildcard, every subdomain needs its own certificate order and this issue becomes a
duplicate of D6.

**Depends on:** D1, D2, #58

---

### D4 · `Storefront: Custom domain registration and DNS instructions`

#### What
The agent-facing API to add, list and remove a custom domain, and the DNS records they must
create to prove they own it.

#### Acceptance criteria
- [ ] `POST /api/agent/sites/{id}/domains` accepts a hostname, normalises and validates it through
      D2, and creates a `pending` row
- [ ] A `verification_token` is generated per domain — cryptographically random, at least 128 bits,
      never derived from `agency_id` or the hostname
- [ ] The response contains the exact records to create, ready to copy: TXT at
      `_trips-verify.<hostname>` with the token, and the routing record (see below)
- [ ] Claiming a hostname that already exists on another agency returns a clear `409`, and does not
      reveal which agency holds it
- [ ] `GET` lists the agency's domains with verification and SSL status
- [ ] `DELETE` removes an unverified domain outright; removing a *verified* one is D8's problem
- [ ] Re-requesting instructions returns the same token — regenerating it every time an agent
      reloads the page means their DNS never matches
- [ ] A per-agency ceiling on domains, configurable, so the table cannot be used as free storage
- [ ] A single entitlement check point marked in the code for `custom_domain`, even though nothing
      enforces it in M2 — see [finding 6](#6-the-custom_domain-entitlement-does-not-exist-until-m3)

#### Two records, not one
The epic body says "TXT/CNAME verification", which reads as though those are alternatives. They
are two different jobs and an agent needs both:

- **TXT** at `_trips-verify.<hostname>` proves they control the zone
- **CNAME** (or A/ALIAS at an apex) sends traffic to us

Ownership without routing is a verified domain that serves nothing. Routing without ownership lets
anyone point a hostname at us and claim it. D5 checks both before it calls a domain verified.

The apex case is not solved and needs a decision — see
[finding 2](#2-apex-domains-cannot-use-a-cname-and-the-alternative-is-an-infrastructure-commitment).
Until it is, this issue should support `www.` and other subdomain hostnames, and return a clear
"apex domains are not supported yet" for a bare `example.com` rather than issuing instructions
that cannot work.

**Depends on:** D1, D2

---

### D5 · `Storefront: IDnsResolver port and the DNS verification job`

#### What
Job 13 in [the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md): resolve the records D4 asked for,
record every attempt, and move the domain to `verified` when both are present.

#### Acceptance criteria
- [ ] `IDnsResolver` port in Application, implemented in Infrastructure — so tests never make a
      real DNS query, and a resolver library choice is not baked into business logic
- [ ] Hangfire job on the plan's cadence: every 5 minutes, backing off to hourly, abandoning at
      7 days with `verification_status = abandoned`
- [ ] Queries **authoritative** nameservers where possible, not a recursive cache. An agent who
      fixes a typo should not wait out a stale negative TTL, and cached NXDOMAIN is exactly the
      case that turns "I've added it, why doesn't it work" into an afternoon
- [ ] Queries more than one resolver and requires agreement, so mid-propagation state does not
      verify a domain that half the internet cannot reach
- [ ] Writes one `site_domain_checks` row per attempt — including failures, with the resolver used
      and the values actually observed. This table is the answer to "why isn't my domain working",
      and it is only useful if it records the misses
- [ ] Verifies **both** the TXT token and the routing record before transitioning to `verified`
- [ ] Token comparison is exact after normalisation — TXT values arrive quoted and may be chunked
      at 255 characters
- [ ] Handles a hostname with multiple TXT records: one matching record is enough, and unrelated
      records (SPF, other vendors' verification) are ignored, not treated as a mismatch
- [ ] A manual "check now" trigger, rate-limited, so an agent who has just fixed their DNS is not
      told to wait an hour
- [ ] Integration test with a stubbed resolver covering match, mismatch, NXDOMAIN, timeout, and
      the 7-day abandon

**Depends on:** D4

---

### D6 · `Storefront: ICertificateProvisioner, SSL issuance and T-30 renewal` 🚫

#### What
Job 14 in the plan: get a certificate for a verified custom domain, and replace it before it
expires.

#### This issue is blocked on a decision
`ICertificateProvisioner` is named in [the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) as a
cloud-agnostic port, and the port is the easy part. What sits behind it — an ACME client we run
ourselves, or a managed edge that does it for us — is an unmade infrastructure decision that
changes this issue completely, and it is described in
[finding 3](#3-icertificateprovisioner-has-no-chosen-implementation-and-the-decision-is-not-ours-to-defer).
**Do not start this issue until it is made.** The acceptance criteria below hold either way; the
work behind them does not.

#### Acceptance criteria
- [ ] `ICertificateProvisioner` port in Application: request, poll status, install, revoke
- [ ] Issuance is triggered by D5's verification, not polled into existence
- [ ] `ssl_status` and `ssl_expires_at` are maintained on `site_domains` whichever implementation
      is chosen — the agent-facing status must not depend on the provider
- [ ] A daily scan issues a renewal for anything expiring within **30 days**, per the epic
- [ ] Renewal failure escalates on a schedule — retrying quietly until the certificate expires is
      how an agent's site goes down without warning. Alert at T-30, T-14, T-7 and daily inside
      T-3 (D10 owns the alerting itself)
- [ ] Issuance is **not** retried blindly. Let's Encrypt's rate limits are per registered domain,
      so a retry loop against one permanently misconfigured domain exhausts the limit for every
      agent at once, not just the broken one. Exponential backoff and a failure ceiling
- [ ] Only one instance may order a certificate for a given hostname at a time — a distributed
      lock, since the API runs multi-instance
- [ ] Private keys go through `ISecretStore`, never the database and never a mounted file
- [ ] A removed domain (D8) has its certificate revoked or released
- [ ] Integration test against a stub provisioner covering issue, renew, and a failure that
      escalates

#### Notes
Whatever is chosen, the T-30 window is not negotiable and neither is the alerting. A certificate
expiring on a live custom domain is the most visible failure this platform can have — the
traveller gets a full-page browser security warning on the agent's own brand, and the agent finds
out from their customer.

**Depends on:** D5, and the decision in finding 3

---

### D7 · `Storefront: Host-header tenant resolution and the Redis host map`

#### What
The middleware that turns a `Host` header into a `site_id` and an `agency_id`. The plan calls
`site_domains` the hottest lookup in the system; this is the thing doing the looking.

#### Acceptance criteria
- [ ] Middleware resolves `Host` → `(site_id, agency_id, site status, agency status)` through the
      single audited repository method from D1
- [ ] Redis-cached with the plan's **5-minute TTL**, invalidated on publish and on any change to
      a domain row
- [ ] Cache key is the normalised hostname from D2. An unnormalised key means `Booking.Zara.com`
      and `booking.zara.com` are two entries that can disagree
- [ ] **Negative results are cached too**, with a shorter TTL. Otherwise every request for a
      hostname that does not exist is a database query, and pointing traffic at random hostnames
      becomes a trivial way to load the database
- [ ] Unknown host → `404`, per the plan. Never a redirect to a default site, and never an error
      page carrying our brand
- [ ] Suspended agency → maintenance page (FRD §2.15 RS-3). D8 owns the states; this owns the
      middleware honouring them
- [ ] The resolved tenant flows into `ITenantContext` so everything downstream is filtered
      normally — the bypass ends at this middleware and goes no further
- [ ] The `Host` header is validated before use: length-capped, normalised, port stripped. It is
      attacker-controlled input and it is about to become a cache key and a query parameter
- [ ] Redis being unavailable falls through to the database rather than failing the request, and
      says so in a metric
- [ ] Integration test: two agencies, two hostnames, and a request to each returns only its own
      agency's data — plus a test proving a stale cache entry is purged on domain removal

#### Notes
This is the piece [#60](https://github.com/innovateavitech/trips-agent/issues/60) cannot start
without, and it needs neither DNS nor a certificate to build or test. Worth pulling forward.

It also needs a registered Redis client, which — as
[the #71 breakdown found](0071-security-hardening.md#what-this-breakdown-found) — **no issue
owns**. That has not changed.

**Depends on:** D1, and Redis registration

---

### D8 · `Storefront: Primary domain, removal and suspension lifecycle`

#### What
The states a domain moves through after it works: which one is canonical, what happens when the
agent removes it, and what happens when the agency is suspended.

#### Acceptance criteria
- [ ] Exactly one primary domain per site, enforced by a partial unique index — not by application
      code, which loses the race between two concurrent updates
- [ ] Reconcile `sites.primary_domain_id` with `site_domains.is_primary`, which are currently two
      records of the same fact — see [finding 4](#4-the-primary-domain-is-recorded-in-two-places)
- [ ] Non-primary verified domains 301 to the primary, so the same content is not served on two
      hostnames and the agent's search ranking is not split between them
- [ ] A verified domain cannot be removed without confirmation, and removal purges the Redis
      entry, releases the certificate and leaves the subdomain serving. An agent must not be able
      to take their own site fully offline by deleting a row
- [ ] The free subdomain cannot be removed at all — it is the fallback
- [ ] Suspending an agency takes every hostname to the maintenance page within one cache TTL, and
      unsuspending restores it. This is FRD §2.15 RS-3 and it is a requirement, not a nicety
- [ ] The maintenance page carries the **agent's** branding, not ours
      ([CLAUDE.md rule 4](../../CLAUDE.md#4-nothing-traveller-facing-may-reference-trips)) — a
      traveller who hits it must not learn that Trips exists
- [ ] Every state change is written to the platform audit log
      ([#21](https://github.com/innovateavitech/trips-agent/issues/21))

**Depends on:** D3, D4, D7

---

### D9 · `Storefront: Agent console — the Domains screen`

#### What
Where an agent connects a domain, and where they find out why it has not worked.

#### Acceptance criteria
- [ ] Lists domains with verification and SSL status, and which is primary
- [ ] Add-domain flow shows the exact DNS records with a copy button per value. Most agents will
      paste these into a registrar control panel they log into twice a year — a value they have to
      retype is a value they will get wrong
- [ ] Shows the check history from `site_domain_checks` in plain language: what we looked for,
      what we found, when we last looked, and when we will look again
- [ ] "Check now", rate-limited, wired to D5's manual trigger
- [ ] Registrar-specific guidance for the common Nigerian registrars, at minimum a note that the
      record name may need to be entered without the domain suffix — the single most common way a
      TXT record ends up at `_trips-verify.example.com.example.com`
- [ ] SSL status is shown separately from verification status, because a domain can be verified
      and not yet certificated, and those need different advice
- [ ] Uses `packages/ui` components and design tokens only. `pnpm check:design` passes
      ([CLAUDE.md rule 7](../../CLAUDE.md#7-never-hard-code-a-colour-font-or-spacing-value))
- [ ] Empty state explains what a custom domain is and what setting one up will require of them

**Depends on:** D4, D5, D6, #48

---

### D10 · `Storefront: SSL and DNS failure alerting and runbook`

#### What
The alerting behind D6's escalation, and the runbook entry
[the plan already expects to exist](../ARCHITECTURE_AND_DELIVERY_PLAN.md) under
`docs/runbooks/` — "what to do when SSL provisioning fails".

#### Acceptance criteria
- [ ] Alerts to the platform team when: issuance fails past its ceiling, a renewal has not
      succeeded inside T-14, any certificate reaches T-3, or a verified domain starts failing DNS
      checks (an agent changing their DNS after verification)
- [ ] Alerts to the **agent**, in their own console and by email, for the things they can fix —
      with the fix in the message, not a support address
- [ ] `docs/runbooks/ssl-provisioning-failure.md`: how to tell rate-limiting from a
      misconfiguration from a provider outage, how to force a re-issue, how to check what the
      resolver actually sees, and what to tell the agent
- [ ] A dashboard or query that answers "which domains expire in the next 30 days and have not
      renewed" without reading logs
- [ ] The runbook is written from a real failure reproduced in staging, not from imagination

**Depends on:** D5, D6

---

## What this breakdown found

Six things. Two of them change work that happens before this epic is picked up.

### 1. The host→tenant lookup is a fourth legitimate tenant-filter bypass

[CLAUDE.md rule 3](../../CLAUDE.md#3-never-bypass-the-tenant-filter) says there are "two or three
legitimate uses in the whole codebase, all in platform-admin reporting, all audited". This is a
fourth, and it is not admin reporting — it is on the hot path of every anonymous storefront
request. Resolving a hostname to a tenant is *how the tenant is discovered*, so by definition it
cannot run inside the tenant filter.

The risk is not the bypass itself. It is that whoever builds D7 will hit the filter, reach for
`.IgnoreQueryFilters()`, and add an unaudited untenanted read of a table containing every agency's
hostnames — which is a fine way to leak the customer list. D1 addresses it by making the bypass a
single named method with a fixed projection and a test.

It also affects [#12](https://github.com/innovateavitech/trips-agent/issues/12): under RLS that
read returns nothing on a connection with no `agency_id` set, so #12 needs an explicit carve-out
for `site_domains` rather than discovering the problem when the storefront 404s for everything.
**Worth a comment on #11 and #12 now, while both are unstarted.**

### 2. Apex domains cannot use a CNAME, and the alternative is an infrastructure commitment

The epic says "TXT/CNAME verification". A CNAME cannot exist at a zone apex — it cannot coexist
with the SOA and NS records that are required there. So `bookwithzara.com` cannot CNAME to us, and
"I want my domain, not `www` in front of it" is what most agents will want.

The three ways out are all commitments:

- **ALIAS/ANAME at the agent's registrar** — free for us, but support is patchy, and it pushes the
  problem onto an agent using a registrar control panel they barely know
- **An A record to a stable IP we own** — works everywhere, and pins us to that IP address more or
  less permanently. Agents' DNS is not something we can ask to be changed later
- **Require `www.` plus a registrar-level apex redirect** — honest and cheap, and some agents will
  not accept it

This is not one of the 27 open questions and it should be. It needs answering before D4 is
written, because it determines what the DNS instructions say. D4 currently proposes shipping
subdomain-only support with a clear message, which is the option that commits us to nothing.

### 3. `ICertificateProvisioner` has no chosen implementation, and the decision is not ours to defer

The plan lists the port under cloud-agnostic ports, alongside a note that cloud is not chosen yet.
For `IBlobStorage` that deferral is cheap. For certificates it is not, because the two shapes are
not swappable:

- **We run an ACME client** (Certes or similar) — full control, no per-domain cost, and we own key
  storage, distributed locking on orders, rate-limit management against Let's Encrypt, the
  challenge endpoint, and the renewal machinery. Roughly D6 as written, plus operational burden
  forever
- **A managed edge does it** (Cloudflare for SaaS, ACM + CloudFront, Azure Front Door) — issuance,
  renewal and termination handed off, D6 shrinks to an API call and a status poll, and we take a
  per-domain cost and a lock-in

There is a second-order effect worth naming: with HTTP-01 or TLS-ALPN-01, a certificate cannot be
issued until traffic already reaches us, which means the routing record must be live *before*
issuance — this is why D4 asks for two records and D5 checks both. DNS-01 would avoid that, but
DNS-01 requires control of the agent's zone, which we do not have and should not ask for.

**Recommendation: a managed edge for MVP**, behind the port, because the failure mode of getting
certificate operations wrong is an agent's live site showing a browser security warning. This
wants an ADR, and it wants deciding well before D6 is scheduled.

### 4. The primary domain is recorded in two places

[§2.4 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) gives `sites` a `primary_domain_id` and
`site_domains` an `is_primary`. Two records of one fact, and nothing stops them disagreeing.

**Suggested:** keep `sites.primary_domain_id` as authoritative — it makes "one primary per site"
structural rather than a constraint to enforce — and either drop `is_primary` or keep it with a
partial unique index and a trigger. D8 owns reconciling it; D1 should not build both without
deciding.

### 5. Open question 20 is half answerable and half blocked

[Open question 20](../ARCHITECTURE_AND_DELIVERY_PLAN.md) asks for "a reserved-word denylist plus
manual review against a known-brand list — and someone must supply that list".

The denylist half is engineering, and D2 does it. The manual-review half cannot be built, because
nobody has supplied a brand list and nobody owns working the queue. Per
[CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), that is flagged rather than guessed at.

**Suggested:** ship D2's denylist for MVP and accept that a determined squatter gets through, but
add the seam now — subdomain claims land in a `pending_review` state that currently auto-approves,
so turning review on later is a configuration change rather than a schema migration. The questions
that need answering are who supplies the list and who works the queue. Both are client answers.

### 6. The `custom_domain` entitlement does not exist until M3

`custom_domain` is an entitlement in [§2.3](../ARCHITECTURE_AND_DELIVERY_PLAN.md), and
entitlements ship in M3 with [#64](https://github.com/innovateavitech/trips-agent/issues/64). So
throughout M2, every agent on every tier can connect a custom domain and nothing stops them.

That is probably fine for M2 — but it should be a decision rather than an accident, because
turning enforcement on later takes domains away from agents already using them. That is
[open question 15](../ARCHITECTURE_AND_DELIVERY_PLAN.md), whose recommendation is to grandfather
existing usage, block new usage, and give 30 days' notice. D4 asks for a single marked check point
so M3 has exactly one place to change.

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#59](https://github.com/innovateavitech/trips-agent/issues/59)
3. Create D1–D10 with `module:storefront` + `M2` (D6 also `blocked` + `needs-decision`), rewriting
   the `Depends on:` lines with real numbers
4. Comment on [#11](https://github.com/innovateavitech/trips-agent/issues/11) and
   [#12](https://github.com/innovateavitech/trips-agent/issues/12) about the `site_domains` bypass
   (finding 1) — both are unstarted, which is the cheapest moment to change them
5. Raise the apex-domain question (finding 2) as a 28th open question, or get it answered
6. Open an ADR for the certificate strategy (finding 3) and decide it before D6 is scheduled
7. Take findings 5 and 6 to the client with the other M2 blocking questions
8. `./scripts/generate-backlog.sh`
