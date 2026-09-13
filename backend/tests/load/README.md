# Load test: the search endpoint

Issue 109. A repeatable load test of `POST /api/v1/search/flights`, reported separately for a
**cold** cache (every request reaches the supplier) and a **warm** one (the net-rate cache answers).

The last run's numbers, and what they mean, are in
[docs/LOAD_TEST_SEARCH.md](../../../docs/LOAD_TEST_SEARCH.md). This file is how to run it.

```bash
backend/tests/load/run.sh cold     # about five minutes
backend/tests/load/run.sh warm     # about five minutes
backend/tests/load/run.sh down     # stop the stack and wipe its data
```

Needs Docker and the .NET SDK. k6 runs in a container unless a local `k6` is on the `PATH`.
Results land in `results/` (git-ignored): k6's summary as JSON, the resource samples as CSV, and
the API's log.

---

## The rules this test obeys

- **The supplier is never the real Trips Africa.** It is WireMock, in `wiremock/`. Trips Africa
  publish no rate limits and no concurrency ceiling (open question 27), so peak load against their
  staging would be an incident, not a test. `run.sh` cannot reach them: the profile points the
  supplier base URL at the stub, with a placeholder credential.
- **It is not a CI job.** Four minutes of load on every pull request costs four minutes and proves
  nothing repeatable about a shared runner. This is run by hand, before a release, and the numbers
  are written up.
- **Nothing here is a production dependency.** No application code knows the load test exists;
  everything it needs is configuration.

## What is here

| | |
|---|---|
| `run.sh` | The whole run: stack up, build, migrate, seed, API up, sample, k6, summarise |
| `search.js` | The k6 scenario — two scenarios, `peak` and `stress`, reported apart |
| `profile.env` | The load-test profile: connection strings, the stub's URL, the raised limits |
| `docker-compose.yml` | Its own PostgreSQL, Redis, RabbitMQ and WireMock, on ports of their own |
| `wiremock/` | The supplier's recorded response shapes and the latency injected into them |
| `sample.sh`, `summarise-resources.sh` | PostgreSQL and Redis every two seconds, and the peaks |

## The knobs

| Variable | Default | |
|---|---|---|
| `PEAK_RPS` | 20 | The expected busy-hour rate. Where that number comes from is in the write-up |
| `STRESS_RPS` | 40 | Twice the peak, to show what is left over |
| `PEAK_DURATION` | `3m` | |
| `STRESS_DURATION` | `1m` | |

```bash
PEAK_RPS=50 STRESS_RPS=100 PEAK_DURATION=2m backend/tests/load/run.sh cold
```

The supplier's injected latency lives in `wiremock/mappings/*.json`
(`delayDistribution`: log-normal, median 1.5 s domestic and 3 s international). Change it there when
real `supplier_api_calls.latency_ms` figures arrive, and re-run.

## Rate limiting

Rate limiting stays **on** — its Redis round trips are part of what is being measured. Two limits
are raised in `profile.env`, because one signed-in user in the test stands in for hundreds of real
agents: the per-user search limit (30 a minute) and the per-agency ceiling (1,200 a minute).
Nothing is disabled, and no production default is touched. If a run reports
`search_rate_limited > 0`, the load has outgrown those numbers — raise them in the profile rather
than turning the limiter off.

## Reading the output

```
  peak    p50 15 ms   p95 36 ms   p99 64 ms   max 275 ms   errors 0.00%
  served from cache: 98.0%
```

- `peak` / `stress` — the two rates, never mixed together.
- `served from cache` — the API's own `fromCache` flag on each response, so it is the search
  cache's hit rate and not Redis's overall one.
- The resource summary underneath names the peak PostgreSQL connection count against the pool's
  100, Redis memory, and the API process's CPU and memory.
- `*-database.txt` splits each cold search into the supplier's time and our own, from the audited
  supplier calls.
- k6 exits 99 when a threshold is crossed (p95 over 5 s, or errors over 1 %). `run.sh` reports that
  and still writes the results — a missed target is a finding, not a broken run.
