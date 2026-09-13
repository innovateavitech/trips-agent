# Penetration test — scope of work

Issue 110. This is what a tester is commissioned to do, written before anyone is commissioned so
that the scope is ours and not the vendor's boilerplate.

**Status: not done.** No external test has been run. This document, the environment recipe beside
it ([TEST_ENVIRONMENT.md](TEST_ENVIRONMENT.md)) and the internal pass we ran ourselves
([INTERNAL_ADVERSARIAL_PASS.md](INTERNAL_ADVERSARIAL_PASS.md)) are the preparation. **The external
test and its retest are a launch gate** — see the bottom of this file.

---

## 1. What is being tested

The Trips Agent Platform: a B2B2C travel SaaS where Trips sells to travel agencies, and each agency
sells on to its own travellers under its own brand. Three kinds of user, and the boundaries between
them are the point:

| | |
|---|---|
| **Trips staff** (back office) | Approve agencies, suspend them, approve payouts. Hold `platform.*` permissions |
| **Agency staff** | Search, book, run a catalog, take money. Scoped to one `agency_id` |
| **Travellers** | Anonymous. Buy on an agency's storefront, and come back to a booking by a magic link |

**The highest-impact bug class in this system is one agency reaching another's data**, and a generic
web test will not look for it. It is called out separately in §3.

## 2. Surfaces in scope

| Surface | Where | Notes for the tester |
|---|---|---|
| **Agent console** | React SPA on the agency subdomain | The whole agency-facing product: bookings, wallet, catalog, CRM, sub-agents, payouts |
| **Admin console** | React SPA, Trips staff only | KYB review, agency lifecycle, payout approval. Reaching any of it as an agency user is a critical finding |
| **Storefront** | Next.js, one site per agency, on the agency's own domain or a subdomain | Anonymous. Nothing on it may name Trips |
| **Public API** | `/api/v1/public/*` — cart, checkout, departures, trip requests, quotes, manage-my-booking | Anonymous, resolved by `Host` header. Host confusion is in scope |
| **Authenticated API** | `/api/v1/*` | JWT access tokens, rotating refresh tokens |
| **Webhooks** | `POST /api/v1/webhooks/paystack` | Signature forgery, replay, and anything reachable without a signature |
| **Magic links** | Manage-my-booking, document downloads, quote pages, storefront previews, password reset, invitations | Token entropy, expiry, enumeration, whether one link opens another's booking |
| **Hangfire dashboard** | `/hangfire` | Off by default; where it is on, reaching it unauthenticated is a finding. Jobs there move money |
| **Multi-tenancy** | Everywhere | §3 |

Also in scope: the usual OWASP Top 10 work — injection, XSS on agency-authored content (product
descriptions, site blocks), CSRF, SSRF from the custom-domain and asset features, file upload
(KYB documents, dispute evidence, catalog images), rate limiting and account lockout, session and
token handling, and information disclosure in errors.

## 3. Multi-tenancy is explicitly in scope

The tester is given **two unrelated agencies** and asked to make one read or write the other's data.
The environment recipe seeds them, with a third as a sub-agent of the first.

Ask specifically for attempts at:

- **Ids in URLs.** Every id the console shows — order, booking, document, asset, product, departure,
  customer, lead, quote, payout, bank account, dispute, report job, sub-agency — presented to the
  API while signed in as the other agency. The answer must be "not found", never the row and never
  a mutation.
- **The tenant claim.** The `agency_id` in the JWT, in a request body, in a query string, in a
  header. Nothing may take an agency id from the caller.
- **Host confusion.** The storefront picks a tenant from the `Host` header: forwarded headers,
  absolute-URI request lines, a `Host` that names another agency's domain.
- **Net rate and markup.** What Trips charges the agency must never reach a traveller, and must not
  reach a sub-agent whose principal denied `margin.view`. Look in search results, quotes, orders,
  invoices, vouchers, emails, analytics, CSV exports and network reports.
- **Principal and sub-agent.** A sub-agent raising its own wallet allowance, widening its own
  scopes, removing its own permission overrides, or reading its principal's books.
- **Privilege escalation.** An agency user granting themselves a permission they do not hold,
  reaching `platform.*` endpoints, or a Support-level back-office account doing a Finance Admin's
  work (approving a payout, in particular: the approver may never be the requester).

Behind every tenant filter there is PostgreSQL row-level security as a backstop
([ADR-0006](../adr/0006-row-level-security-backstop.md)). **The test environment runs as the
application role, exactly as production does**, so a finding there is a real finding.

## 4. Explicitly out of scope

- **The real Trips Africa supplier API.** Their staging is stubbed in the test environment. They
  publish no rate limits and no concurrency ceiling, and a real ticket cannot easily be refunded.
  Anything that would issue a ticket is out.
- **Paystack live mode.** The environment uses Paystack **test** keys. Do not attempt payments
  against live keys, and do not test Paystack's own systems — they are a third party with their own
  disclosure programme.
- **Denial of service and volumetric load.** Rate limits may be probed to show they exist; sustained
  load is not the point and would only measure the laptop or the test host. Search capacity is
  measured separately ([the load test](../LOAD_TEST_SEARCH.md)).
- **Anything outside the named environment.** No production, no developer machines, no CI, no Trips
  staff email accounts. No social engineering and no physical testing.
- **Third-party infrastructure** we do not own — the hosting provider, the DNS registrar, the
  certificate authority.

## 5. Rules of engagement

- **Environment:** the seeded non-production environment in [TEST_ENVIRONMENT.md](TEST_ENVIRONMENT.md),
  rebuilt for the test and destroyed afterwards. Nothing in it is real: no real traveller, no real
  money, no real ticket.
- **Window and contact:** agreed in writing before the test, with one named engineer on call and one
  named person at the vendor. Anything that looks like a live-money path stops and is reported at
  once.
- **Data handling:** findings, screenshots and the report are treated as confidential; no
  environment data leaves the tester's own storage; everything is destroyed on acceptance.
- **Credentials:** given, not guessed. The tester gets two agency owners, one agency staff member,
  one sub-agent, one Support back-office account and one Finance Admin. Testing authentication
  itself (lockout, reset, OTP, token handling) is in scope with those accounts.
- **Retest:** included in the engagement, not quoted separately. A fix nobody re-tested is a claim.

## 6. How findings come back

**Individual issues, not one PDF.** Each finding becomes a GitHub issue labelled `module:security`
with a severity label (`severity:critical`, `severity:high`, `severity:medium`, `severity:low`),
carrying: the surface, the exact request that shows it, the impact in one sentence, and how the
tester would fix it. One issue per finding, assignable and closable on its own. The vendor's own
report is attached to a tracking issue for the record.

Severity is **ours to agree, not the vendor's to declare**. The scale we use:

| | |
|---|---|
| **Critical** | One agency reads or writes another's data; anyone reaches money or tickets; authentication bypass |
| **High** | Traveller PII exposed; net rate or markup leaked; privilege escalation within a tenant; an unauthenticated write |
| **Medium** | Information disclosure with a precondition; a missing control with a compensating one behind it |
| **Low** | Hardening, headers, verbose errors |

## 7. The launch gate

**Not done, and blocking launch:**

- [ ] A tester commissioned, the scope above agreed and signed
- [ ] The environment rebuilt and handed over with its credentials
- [ ] The test run
- [ ] Every Critical and High **fixed**, or accepted in writing by the repository owner with the
      reason recorded
- [ ] Medium and Low triaged, each with a decision recorded on its issue
- [ ] **A retest** confirming the fixes landed, and its report attached

Until every box above is ticked, this platform has not been penetration-tested. What has been done
is the internal pass in [INTERNAL_ADVERSARIAL_PASS.md](INTERNAL_ADVERSARIAL_PASS.md), which is our
own work marking our own homework — useful, and not a substitute.
