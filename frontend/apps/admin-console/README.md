# Admin console — the Trips back office

The internal console **Trips staff** use to run the platform. It is not the agent console
(`apps/agent-console`, which travel agencies use) and it is never seen by a traveller.

Today it holds the first slice of epic #66: **KYB review** — the queue of agencies waiting to be
verified, the documents each one uploaded, and the approve / reject decision. Until an agency is
approved it cannot fund its wallet or sell anything, so this queue is the gate every new customer
waits behind.

---

## 1. Running it

The console has no data of its own. It talks to `TripsAgent.Api`, so the API and its database have
to be up first. From the **repository root**:

```bash
docker compose up -d                                          # Postgres, Redis, RabbitMQ, MinIO, Mailpit
cd backend
dotnet run --project services/TripsAgent.Api -- migrate       # create the schema
dotnet run --project services/TripsAgent.Api -- seed          # staff and agency accounts
dotnet run --project services/TripsAgent.Api                  # the API, on http://localhost:5002
```

Then, in a second terminal:

```bash
cd frontend
pnpm install
pnpm --filter admin-console dev                               # http://localhost:5174
```

Open <http://localhost:5174>.

### Why the dev-server proxy is not optional

The console calls **relative** `/api/...` URLs, and Vite forwards them to `http://localhost:5002`
(see `vite.config.ts`). Two reasons it has to work this way:

- **The API has no CORS configured.** A page served from `localhost:5174` is not allowed to read a
  response from `localhost:5002`. Behind the proxy the browser only ever talks to its own origin,
  so the question never arises. Production CORS and cookie hardening are issue 107.
- **Port 5000 is taken.** macOS AirPlay Receiver holds it, which is why the API listens on 5002.

Document links are signed, relative paths (`/api/v1/admin/kyb/documents/{id}?expires=…&signature=…`)
and travel through the same proxy, so they open in a new tab without an `Authorization` header.

---

## 2. Signing in

Seeded by `DatabaseSeeder` / `IdentitySeedData`. Every seeded account shares the password
`Password123` — a published constant that exists so a fresh clone can sign in, only ever written to
a local database, and not a secret.

| Account | Role | Use it for |
| --- | --- | --- |
| `ops@tripsagent.test` | Operations Admin | **The demo account.** Holds `kyb.review` |
| `admin@tripsagent.test` | Super Admin | Everything, including permissions Operations lacks |
| `owner@lagostravel.test` | Agency Owner | Checking that an agency account is *refused* here |

Sign in as **`ops@tripsagent.test`**.

An agency account is turned away on the sign-in screen itself: the console reads the token's
claims, sees an `agency_id` and no platform permission, revokes the session it was just issued and
explains that agencies use the agent console. None of that is the security boundary — the API
checks `kyb.review` on every request and answers 403 without it. The console checks only so it can
explain a refusal before the reviewer hits it.

### Putting agencies in the queue

The seed leaves one agency (`Pending Travel`) waiting. For a fuller queue:

```bash
pnpm --filter admin-console demo:seed
```

It registers three more agencies through the **real public endpoints** — register, verify the email
via Mailpit, upload documents, submit — so what the reviewer sees is what the live flow produces,
including real file sizes and checksums. It needs the API on :5002 and Mailpit on :8025, and it
only ever talks to localhost. Nothing is written to the database directly.

---

## 3. The demo click-path

About two minutes.

1. **Sign in** at <http://localhost:5174> as `ops@tripsagent.test` / `Password123`.
   You land on the KYB queue; the sidebar badge counts what is waiting.
2. **The queue.** Oldest first — a first-in, first-out line, not a stack. Point out the *Waiting*
   column: it turns amber at 24 hours and red past the 48-hour review target, and anything overdue
   raises a banner at the top. Try the **All waiting / Submitted / Under review** filter; the choice
   lives in the address bar, so Back and reload keep it.
3. **Open the oldest submission.** Left: the agency's legal and trading name, country, and both
   statuses. Right: the document viewer.
4. **Read the documents.** Click each one in the list — PDFs and images preview inline, and the list
   ticks off the ones you have opened. Links are signed and expire after ten minutes; the page
   quietly refetches a minute before the earliest one dies, so a link never goes dead under you.
5. **Reject first**, to show the mandatory reason. Press *Reject* and then *Continue* with the box
   empty — it is refused, and so is a one-word reason, because the agency has to be told what to
   fix. Write a real one; the confirmation step quotes it back exactly as the agency will read it
   in its email. Press *Edit reason*.
6. **Approve instead.** Cancel back, press *Approve*, and confirm. The result says the agency is
   verified and offers **Review next** — the next-oldest submission, so a reviewer can work the
   queue without going back to it.
7. **Sign out** from the user menu, top right, which also shows which account is signed in — a real
   question on a shared operations desk.

Worth mentioning if asked: a decision is **never retried automatically**. A 409 ("someone else
decided first") is a clean no, but a dropped connection is an *unknown outcome*, and the screen says
so and sends the reviewer to refresh rather than inviting a second rejection on top of the first.
Same reasoning as ADR 0003 applies to ticket issuance.

---

## 4. How the code is laid out

Follows the agent console's wallet feature, so the two apps read alike.

```text
src/
  app/            the shell every signed-in screen sits in — sidebar, top bar, user menu
  components/     page frame, loading / empty / error states, icons
  features/
    auth/         sign-in, the session context, the route guards
    kyb-review/   the queue and the submission detail
      *-rules.ts      plain, tested functions: ordering, ageing, reason validation, failures
      *-api.ts        one interface per feature; screens depend on it, not on fetch
      pages/ components/
  lib/
    api/          the one place the console talks HTTP; owns the session's lifecycle
    auth/         claims, the session store, safe redirects
```

Two things are deliberate and easy to undo by accident:

- **Decision logic lives in `*-rules.ts`, not in components.** Why an agency is first in the queue,
  and why a button is disabled, are values you can read and test rather than conditions buried in
  JSX. 124 vitest tests cover them.
- **Types in `kyb-review/types.ts` are hand-written mirrors** of the C# records in
  `TripsAgent.Contracts`. When `pnpm generate:api` produces `@trips/api-client`, delete that file
  and import from there — two hand-kept copies of one shape is how a field quietly changes meaning
  on one side only.

### Checks

```bash
cd frontend
pnpm verify        # prettier, eslint, typecheck, check:design, tests
```

Design tokens only — no raw colour, font or spacing value anywhere outside
`packages/ui/src/styles/tokens.css`. Need a different look? Add a variant in `packages/ui`.

> A wart worth knowing: `check:design` reads an issue reference like `#107` in a comment as a
> three-digit hex colour. Its comment-skipping filter does not fire, because `grep -n` puts a line
> number in front of the `*`. Write "issue 107" in a comment until that is fixed.

---

## 5. Not built yet

Piece A1 of the #66 breakdown is what exists. Deliberately out of scope:

- **Decision history**, and **"take up for review"** (A2). The queue only ever returns undecided
  submissions, and no endpoint calls `KybSubmission.BeginReview` — so nothing is ever *Under
  review*, and the filter for it is honest about being empty.
- **Session hardening** (A3): the refresh token is in `sessionStorage`, readable by script on the
  page. The fix is an httpOnly cookie the API issues, alongside issue 107.
- The agency directory, profiles, suspend/terminate, back-office users, and the dashboard — A4
  onwards in the breakdown on #66.
