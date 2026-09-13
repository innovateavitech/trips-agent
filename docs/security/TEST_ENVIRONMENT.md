# The environment a penetration tester is given

Issue 110. How to build the non-production environment for a security test, and what the tester is
handed. The scope it serves is [PENETRATION_TEST_SCOPE.md](PENETRATION_TEST_SCOPE.md).

**The rule this whole file exists for: nothing in this environment may reach anything real.** Not a
traveller, not a naira, not a ticket. A tester who finds a way to move money has found a bug worth
paying for; a tester who moves *real* money has cost us an incident, and a real ticket cannot easily
be refunded.

---

## 1. What has to be true before it is handed over

| | Why |
|---|---|
| **A database of its own**, built by `migrate` and `seed` from an empty one | So the tester can break it, and so wiping it costs nothing |
| **The API connects as `tripsagent_app`**, not as the owner | Row-level security is only a backstop if it is switched on ([ADR-0006](../adr/0006-row-level-security-backstop.md)). `/health` reports Degraded when it is not — check it |
| **The Trips Africa supplier is stubbed** | Never their staging, never production. The WireMock stub in `backend/tests/load/wiremock/` answers search; anything else it answers 501 |
| **Paystack is in test mode** | Test secret key only. Test cards move no money. Webhooks point at this environment |
| **Email goes to a catcher** | Mailpit or equivalent. Every address in the seed is a `*.example.com` subdomain, reserved by RFC 2606, so a leaked email reaches nobody |
| **Rate limiting on, at production defaults** | It is part of what is being tested. The load-test profile's raised limits are for load tests and must not be used here |
| **The Hangfire dashboard** set as production will have it | If it is on in production, it is on here — reaching it is a finding |
| **No copy of production data** | There is none to copy yet, and there never will be for this purpose. The seed below is enough |

## 2. Building it

```bash
docker compose up -d postgres redis rabbitmq mailpit

cd backend
dotnet run --project services/TripsAgent.Api -- migrate     # schema and reference data
dotnet run --project services/TripsAgent.Api -- seed        # the accounts in §3
```

Then, as production does — the migration creates the runtime role without a login:

```sql
ALTER ROLE tripsagent_app LOGIN PASSWORD '…';               -- set outside git
```

and point `ConnectionStrings__Postgres` at that role, with `ConnectionStrings__PostgresAdmin` left
as the owner for migrations. `backend/tests/load/profile.env` is a worked example of that split;
its raised rate limits are **not** for this environment.

Serve the API, the Worker, the agent console, the admin console and the storefront as production
does, on hostnames the tester can reach. Give each seeded agency a storefront hostname so the
`Host`-header attacks in the scope have something to aim at.

## 3. The accounts the tester is given

Every one is created by `seed`. The password for all of them is `Password123` — deliberately
obvious, because nothing behind it is real.

| Account | Who they are | Why the tester has them |
|---|---|---|
| `owner@lagostravel.example.com` | Owner, **Agency A** (Lagos Travel, verified) | The full agency product |
| `agent@lagostravel.example.com` | Agent role, Agency A | Holds **no `margin.view`**: the account for "can a colleague see the agency's cost price?" |
| `owner@kanojourneys.example.com` | Owner, **Agency B** (Kano Journeys, verified, unrelated) | **The other side of the tenant boundary.** Attack A from here and B from there |
| `owner@ikejabranch.example.com` | Owner of A's **sub-agent** | The principal/sub-agent rules: allowances, scopes, denied permissions, margin visibility |
| `owner@pendingtravel.example.com` | Owner of an agency **awaiting KYB** | An agency that may not transact at all |
| `admin@tripsagent.example.com` | Trips **Super Admin** | The back office, for comparison — reaching any of it as an agency user is critical |
| `ops@tripsagent.example.com` | Trips **Operations** | Role separation: KYB yes, suspension no |
| `support@tripsagent.example.com` | Trips **Support** (read-only) | The narrowest back-office role |
| `finance@tripsagent.example.com` | Trips **Finance Admin** | The only role that can approve a payout — and never one it requested |

**A and B are not related.** Neither is the other's principal, neither manages the other, and they
share nothing but the database. That is the pairing the scope asks the tester to attack; the
sub-agent is a *third* case, where some visibility is legitimate and the question is which.

### Data seeded beside them

The seeder creates the agencies, their settings and branding, the roles and permissions, and the
back-office accounts. Anything the test needs on top — a published catalog, a storefront site with
a verified hostname, a funded wallet, a booking, a departure with seats, a payout request, a
dispute — is created **through the product's own screens** before the tester arrives, so the rows
look the way real rows look. Write down the ids as you go: the tester needs A's ids to try them as
B, and that list is most of the IDOR work in the scope's §3.

## 4. What the tester may and may not do to it

- **May:** anything that stays inside the environment. Break the data, fill it with rubbish,
  exhaust the seats, guess at tokens, replay webhooks.
- **May not:** point any tool at the real Trips Africa API, at Paystack's live mode, or at any host
  that is not on the agreed list.
- **Stop-and-call:** anything that looks like it reached a real ticket, a live card, or another
  environment. The named engineer in the rules of engagement takes that call.

## 5. Afterwards

Destroy it: `docker compose down -v` for the local build, or delete the hosted environment and its
database outright. Rotate anything that was typed into it — the Paystack test key, the `tripsagent_app`
password, the JWT signing key — on the ordinary principle that a credential which reached a third
party's laptop is not a credential any more. Then rebuild it for the retest, rather than handing
back the one the tester has already been living in.
