#!/usr/bin/env bash
# =============================================================================
#  Run the search load test (issue 109). README.md in this folder explains it.
#
#    backend/tests/load/run.sh cold      every search misses the cache
#    backend/tests/load/run.sh warm      the cache answers
#    backend/tests/load/run.sh down      stop the stack and wipe its data
#
#  Each run takes about five minutes. Results land in backend/tests/load/results/
#  (git-ignored). Needs Docker and the .NET SDK; k6 runs in a container unless a
#  local k6 is on the PATH.
# =============================================================================
set -euo pipefail

MODE="${1:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
BACKEND="$(cd "$HERE/../.." && pwd)"
COMPOSE=(docker compose -f "$HERE/docker-compose.yml")
RESULTS="$HERE/results"
API_DLL="$BACKEND/services/TripsAgent.Api/bin/Release/net10.0/TripsAgent.Api.dll"
BASE_URL="http://127.0.0.1:5090"

case "$MODE" in
  cold|warm) ;;
  down) "${COMPOSE[@]}" down -v; exit 0 ;;
  *) echo "usage: $0 cold|warm|down" >&2; exit 2 ;;
esac

mkdir -p "$RESULTS"

# The profile holds connection strings with semicolons in them, so it is read line by line and
# exported as it stands, rather than sourced as shell.
load_profile() {
  while IFS= read -r line; do
    case "$line" in ''|\#*) ;; *) export "$line" ;; esac
  done < "$HERE/profile.env"
}

echo "==> Starting the load-test stack (Postgres, Redis, RabbitMQ, the supplier stub)"
"${COMPOSE[@]}" up -d --wait >/dev/null 2>&1 || "${COMPOSE[@]}" up -d --wait

echo "==> Building the API in Release"
dotnet build "$BACKEND/services/TripsAgent.Api" -c Release -v quiet -nologo >/dev/null

load_profile

echo "==> Migrating and seeding the load-test database"
dotnet "$API_DLL" migrate >/dev/null
# The migration creates the runtime role without a login (ADR-0006). This database is throwaway,
# so it gets the throwaway password profile.env connects with, read from that connection string.
APP_PASSWORD="${ConnectionStrings__Postgres##*Password=}"
"${COMPOSE[@]}" exec -T postgres psql -q -U trips -d trips_loadtest \
  -c "ALTER ROLE tripsagent_app LOGIN PASSWORD '${APP_PASSWORD%%;*}'" >/dev/null
dotnet "$API_DLL" seed >/dev/null

echo "==> Starting the API on $BASE_URL"
dotnet "$API_DLL" > "$RESULTS/$MODE-api.log" 2>&1 &
API_PID=$!
SAMPLER_PID=""
cleanup() {
  [ -n "$SAMPLER_PID" ] && kill "$SAMPLER_PID" 2>/dev/null || true
  kill "$API_PID" 2>/dev/null || true
}
trap cleanup EXIT

for _ in $(seq 1 60); do
  curl -sf "$BASE_URL/health" >/dev/null 2>&1 && break
  sleep 1
done
curl -sf "$BASE_URL/health" >/dev/null || { echo "The API did not become healthy; see $RESULTS/$MODE-api.log" >&2; exit 1; }

# Every run starts from an empty cache — and from empty rate-limit windows, which live there too.
"${COMPOSE[@]}" exec -T redis redis-cli FLUSHALL >/dev/null

RUN_STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "==> Sampling Postgres, Redis and the API process every 2 seconds"
"$HERE/sample.sh" "$API_PID" > "$RESULTS/$MODE-resources.csv" &
SAMPLER_PID=$!

echo "==> Running k6 ($MODE)"
set +e
if command -v k6 >/dev/null 2>&1; then
  (cd "$HERE" && MODE="$MODE" BASE_URL="$BASE_URL" k6 run --quiet search.js)
else
  # host.docker.internal reaches the API on the host's loopback from inside Docker Desktop.
  docker run --rm -i \
    -v "$HERE:/scripts" -w /scripts \
    -e MODE="$MODE" -e BASE_URL="http://host.docker.internal:5090" \
    -e PEAK_RPS -e STRESS_RPS -e PEAK_DURATION -e STRESS_DURATION \
    grafana/k6:1.3.0 run --quiet search.js
fi
K6_EXIT=$?
set -e

kill "$SAMPLER_PID" 2>/dev/null || true
wait "$SAMPLER_PID" 2>/dev/null || true
SAMPLER_PID=""

# How much of each cold search was the supplier, and how much was us: every supplier call is audited
# with its latency, keyed to the search that made it. Warm runs make no supplier calls after setup.
sleep 5  # the supplier call log is written in batches, a moment behind
echo "==> Supplier time against the search service's own time, from the database"
"${COMPOSE[@]}" exec -T postgres psql -q -U trips -d trips_loadtest -P pager=off <<SQL | tee "$RESULTS/$MODE-database.txt"
select count(*)                                                                  as supplier_searches,
       percentile_disc(0.50) within group (order by c.latency_ms)                as supplier_p50_ms,
       percentile_disc(0.95) within group (order by c.latency_ms)                as supplier_p95_ms,
       percentile_disc(0.50) within group (order by r.latency_ms - c.latency_ms) as service_overhead_p50_ms,
       percentile_disc(0.95) within group (order by r.latency_ms - c.latency_ms) as service_overhead_p95_ms,
       percentile_disc(0.99) within group (order by r.latency_ms - c.latency_ms) as service_overhead_p99_ms
  from supplier.search_requests r
  join supplier.supplier_api_calls c on c.correlation_id = r.id::text
 where r.requested_at >= '$RUN_STARTED';
SQL

"$HERE/summarise-resources.sh" "$RESULTS/$MODE-resources.csv" | tee "$RESULTS/$MODE-resources.txt"

# k6 exits 99 when a threshold is crossed. That is a result to report, not a broken run.
if [ "$K6_EXIT" -ne 0 ] && [ "$K6_EXIT" -ne 99 ]; then
  echo "k6 failed to run (exit $K6_EXIT)" >&2
  exit "$K6_EXIT"
fi
echo "==> Done. Summary: $RESULTS/$MODE-summary.json (thresholds crossed: $([ "$K6_EXIT" -eq 99 ] && echo yes || echo no))"
