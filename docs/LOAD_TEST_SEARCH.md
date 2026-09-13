# Load test: flight search, cold and warm cache

Issue 109. The scenario, the stub and the scripts are in
[`backend/tests/load/`](../backend/tests/load/README.md); this is the run.

**Run on 13 September 2026** on a developer laptop — Apple M5 Pro, 15 cores, 24 GB, macOS 26.6,
with PostgreSQL, Redis, RabbitMQ and the supplier stub in Docker beside the API. **A laptop is not
production.** Everything shares one machine, so the database and the API never cross a network and
never contend with another tenant's traffic, and the numbers below are the best case for the code,
not a forecast of what a deployed environment will do. What the run is good for is the shape of the
answer: where the time goes, whether the cache does its job, and whether anything saturates.

---

## What was run

| | |
|---|---|
| Endpoint | `POST /api/v1/search/flights`, signed in as a seeded agency's owner |
| Expected peak | **20 searches a second**, held for 3 minutes, then **40 a second** for 1 minute |
| Mix | four domestic routes (LOS-ABV, ABV-LOS, LOS-PHC, LOS-KAN) and two international (LOS-LHR, LOS-DXB) |
| Supplier | WireMock, **never Trips Africa** (open question 27) — log-normal delay, median 1.5 s domestic and 3.0 s international, sigma 0.35 |
| Rate limiting | on, with the per-user search limit and the per-agency ceiling raised for the test (see below) |
| Runs | **cold**: every request a combination nobody has searched, so every one reaches the stub. **warm**: sixty popular searches primed first, then repeated |

**Where "expected peak" comes from.** Nobody has given a number (issue 109 says so itself), so this
is the plan's own assumption until the client replaces it: 300 active agencies at launch, three
people signed in per agency in the busy hour, each searching about every 45 seconds —
900 ÷ 45 ≈ **20 searches a second**. 40 a second is twice that, to show what is left over.

---

## The numbers

**Cold — every request reaches the supplier.** 6,002 searches, 0 errors, 0 rate-limited.

| | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 20 searches/s | 1,835 ms | **4,299 ms** | 5,673 ms | 7,998 ms |
| 40 searches/s | 1,865 ms | **4,506 ms** | 5,845 ms | 8,649 ms |

**Warm — the net-rate cache answers.** 6,002 searches, 0 errors, 0 rate-limited, **98.0 % served
from cache** (the 2 % that missed are entries whose four-minute lifetime ran out mid-run, which is
what a steady state really looks like).

| | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 20 searches/s | 15 ms | **36 ms** | 64 ms | 275 ms |
| 40 searches/s | 16 ms | **821 ms** | 3,126 ms | 7,751 ms |

**Where the time goes.** Every supplier call is audited with its own latency, keyed to the search
that made it, so the two halves can be separated. Over the cold run:

| | p50 | p95 | p99 |
|---|---|---|---|
| The supplier | 1,809 ms | 4,345 ms | — |
| **Everything we do** (validate, price every offer, record the search and its offers, cache) | **5 ms** | **9 ms** | 50 ms |

A cold search is the supplier, plus about a hundredth of a second of us.

### Against the FRD's target

FRD §2.3 asks for **5 seconds at p95**.

- **Warm: met, with room to spare** — 36 ms at the expected peak, 821 ms at twice it.
- **Cold: met here, but only because the stub was told to be quick.** 4.3 s at p95 is inside 5 s,
  and it is 4.3 s because the stub's international median was set to 3 s. The measured overhead
  above says the target is decided almost entirely by the supplier: at a p95 supplier latency above
  about 4.9 s the target fails however fast our code is, and a live GDS search over 5 s is ordinary.
  Our own search timeout is 20 s (#33), with one retry, so a single slow search can take 40 s and
  still be a success.

**So: the SLA is only meetable as a post-cache measurement.** That is what this feeds into open
question 16, and the plan's decision 16 now has a number behind it.

### What the load did to the infrastructure

| | Cold, at peak | Warm, at peak |
|---|---|---|
| PostgreSQL connections held by the app role | peak **29** of the Npgsql pool's 100 | peak 6 |
| All PostgreSQL connections | peak 36 of `max_connections` 100 | peak 13 |
| Waiting on a lock | 0 | 0 |
| Redis memory | peak 143 MB | peak 3 MB |
| API process | peak 128 % of one core, 892 MB | peak 94 %, 642 MB |

- **No pool saturation, cold or warm.** Even with sixty searches in flight at once, the pool peaked
  at 29 connections: EF opens a connection per command rather than holding one for the request, so a
  request waiting on a three-second supplier call is holding nothing.
- **Redis memory is the cold run's story:** 143 MB for one agency's four minutes of unique searches.
  A cached entry is a whole net result — fifty international offers is about 75 KB — and the key is
  per agency *and* criteria, so nothing is shared between agencies. At 300 agencies searching this
  hard the cache alone would want tens of gigabytes. The four-minute lifetime is what keeps it
  bounded; whoever sizes the Redis instance should start from "peak searches a minute × 4 × 75 KB"
  and not from a guess.
- **One thing to look at, not a blocker:** Redis served about 70 operations per search
  (~420,000 reads across 6,000 searches, hits and misses roughly even). The rate limiter accounts
  for a handful; the rest is the pricing service being asked to price each of up to fifty offers
  separately. It costs nothing measurable here — Redis is on the same machine — but over a network
  it would be tens of milliseconds a search. Worth a look after the MVP.

---

## Reproducing it

```bash
backend/tests/load/run.sh cold     # about five minutes
backend/tests/load/run.sh warm
backend/tests/load/run.sh down     # stop the stack and wipe its data
```

It builds the API in Release, starts a stack of its own (its own database, its own Redis, the
WireMock supplier), migrates and seeds it, samples PostgreSQL and Redis every two seconds, and
writes k6's summary and the samples to `backend/tests/load/results/` (git-ignored). Nothing about it
runs in CI: a four-minute load test on every pull request would be four minutes nobody gets back,
and the numbers would be whatever the CI runner happened to be doing.

**Rate limiting was configured, not switched off.** `backend/tests/load/profile.env` raises the
per-user search limit (30 a minute) and the per-agency ceiling (1,200 a minute) for the load-test
environment only, because one signed-in user is standing in for hundreds. The limiter still runs, so
its Redis round trips are part of every number above. No production default was changed, and nothing
in the application code knows a load test exists.

## What this run does not tell you

- **Nothing about Trips Africa's real latency or its ceiling.** The stub's delays are an assumption.
  The first production week's `supplier_api_calls.latency_ms` replaces them, and this run should be
  repeated against those numbers.
- **Nothing about several agencies at once.** One agency searched; cache keys, rate limits and
  row-level security all behave differently in the plural, and the cache-memory note above is the
  place that bites first.
- **Nothing about a network.** Database, cache, supplier and API were all on one machine.
- **Nothing about the supplier failing.** The stub answered every call. The circuit breaker, the
  one search retry and the 503 path were not exercised under load.
