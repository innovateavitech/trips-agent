# Running the demo

Everything below runs on one machine, against a real PostgreSQL and Paystack's **test mode**. No
step is a mock-up: the screens call the API, the API writes the ledger, and Paystack really does
take the card.

It takes about five minutes from a clean clone.

---

## 1. Before you start

- **Docker** running (PostgreSQL, Mailpit, RabbitMQ come from `docker-compose.yml`)
- **.NET 10 SDK** and **pnpm**
- Paystack **test** keys, if you want the live top-up — and note *where* they have to be:

  ```bash
  export Paystack__SecretKey=sk_test_…      # from .env, in the shell that runs the API
  export Paystack__PublicKey=pk_test_…
  ```

  `.env` alone is **not** enough. Docker Compose reads that file; `dotnet run` does not, so the
  API starts with no key and the top-up fails with *"the payment provider is not responding"*.
  Export the two variables in the terminal you start the API from.

  Do **not** `source .env` wholesale to achieve that. It carries a placeholder
  `Jwt__SigningKey` which is not valid base64, and it overrides the working development value —
  the API then throws on every authenticated request. Export the two Paystack lines only.

  Without the keys every other step still works; only the top-up stops.

Two machine-level things that will spoil a demo if you meet them cold:

| Symptom | Cause | Fix |
|---|---|---|
| The API cannot bind port 5000 | macOS **AirPlay Receiver** holds it | Nothing to do — the API uses **5002**. Do not "fix" it by moving to 5000 |
| The console looks washed-out grey, brand blue looks flat | Chrome's **force-dark** experiment | Turn off `chrome://flags/#enable-force-dark`, or demo in a clean profile. The app is light-only and declares `color-scheme: light`; that flag deliberately overrides it |

---

## 2. Start it

```bash
docker compose up -d postgres mailpit rabbitmq

cd backend
dotnet run --project services/TripsAgent.Api -- migrate    # schema, then reference data
dotnet run --project services/TripsAgent.Api -- seed       # three demo agencies and their people
dotnet run --project services/TripsAgent.Api               # serves http://localhost:5002
```

Run the API in **its own terminal** and leave it there.

Then, in two more terminals:

```bash
cd frontend
pnpm install
pnpm --filter agent-console dev    # http://localhost:5173  — what an agent sees
pnpm --filter admin-console dev    # http://localhost:5174  — what Trips staff see
```

Each console's dev server proxies `/api` to the API, so the browser never makes a cross-origin
request. That is why no CORS policy is needed yet (issue #107).

Mailpit's inbox is at **http://localhost:8025** — every email the platform sends lands there.

---

## 3. The accounts the seeder creates

Password for all of them: **`Password123`**

| Who | Sign in at | Email |
|---|---|---|
| Trips staff (super admin) | :5174 | `admin@tripsagent.example.com` |
| Trips staff (operations) | :5174 | `ops@tripsagent.example.com` |
| A verified agency | :5173 | `owner@lagostravel.example.com` |
| Its sub-agent | :5173 | `owner@ikejabranch.example.com` |
| An agency still awaiting KYB | :5173 | `owner@pendingtravel.example.com` |

> The addresses are subdomains of `example.com`, which is reserved by RFC 2606 — mail to them
> cannot reach anyone, and Paystack's validator still accepts them. Addresses ending in `.test`
> look safer but Paystack refuses them, which is why they changed.

---

## 4. The demo, in order

**a. An agency that cannot trade yet.** Sign in at :5173 as `owner@pendingtravel.example.com`.
The sidebar shows **Verification**. The screen says what is missing, or — if the seeder already
submitted for them — that the documents are with Trips. Note the wallet: funding is blocked, and
the screen says why in the API's own words rather than failing when pressed.

**b. Staff review it.** In the other browser, sign in at :5174 as `admin@tripsagent.example.com`.
The **KYB queue** lists agencies oldest-first, because the one waiting longest is losing the most
business. Open a submission: the agency's details and every uploaded document, each behind a
freshly signed link. **Approve** it.

**c. The agent's screen answers by itself.** Go back to :5173 without touching anything. Within
about fifteen seconds the page updates: verified, and the wallet is open. Approval is what opens
it — before this the platform never created one, which is the bug the money-path audit found.

**d. Real money, in test mode.** Open **Wallet** → add ₦5,000. You land on Paystack's hosted
page — the card never touches this application, which is what keeps us in PCI SAQ-A. Pay with
Paystack's test card:

```
4084 0840 8408 4081    CVV 408    any future expiry    PIN 0000    OTP 123456
```

You come back to the console and the balance reads **₦5,000.00**, with a statement line behind it.
The credit is not taken from that redirect: the server asks Paystack what happened, and only its
answer credits anything. Paystack's webhook arrives at the same time and is deliberately harmless
— the same event five times still credits once.

**e. If you want to show the books.** The statement, and the nightly ledger integrity audit
(`payment-webhook-drain` and `ledger-integrity-audit` in the Hangfire dashboard at
`/hangfire`, if enabled) prove `wallet balance = SUM(ledger entries)`.

---

## 5. What is live, and what is not

**Live, end to end:** registration and email verification (through Mailpit), sign-in with token
refresh, KYB upload and submission, the staff review queue and its decisions, wallet balance,
top-up through Paystack test mode, the double-entry ledger behind it, and the nightly integrity
audit.

**Not built yet, and shown against a clearly-marked mock layer:** the dashboard's figures, flight
and bus search, the booking flow, the bookings list, and pricing rules. Those screens exist and
behave, but the supplier integration (issues #32–#40) and the checkout saga (#42) are still being
built. Say so rather than letting anyone assume a ticket was issued.

---

## 6. If something goes wrong

| Symptom | What it means |
|---|---|
| `/health` says **Degraded**, mentioning row-level security | Expected locally. The app connects as the docker superuser, which bypasses RLS. [ADR-0006](adr/0006-row-level-security-backstop.md) explains how to run as production does |
| Wallet says **"no wallet yet"** | The database predates the wallet backfill. Re-run `-- migrate` |
| Top-up returns **502** | Almost always the Paystack key missing from the API's *environment* (see §1 — `.env` is not read by `dotnet run`). The API log carries Paystack's own words: `401 Format is Authorization Bearer [secret key]` means no key reached it |
| Every authenticated call 500s with **`Jwt:SigningKey is not valid base64`** | The whole `.env` was sourced into the API's shell. Start a fresh terminal and export only the two Paystack variables |
| The KYB queue is empty | Every submission has been decided. The queue only shows undecided ones today |
| Sign-in works, then every request 401s | The API restarted with a new signing key. Sign in again |
