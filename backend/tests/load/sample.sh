#!/usr/bin/env bash
# Prints one CSV row every 2 seconds until killed: what the load is doing to Postgres, Redis and the
# API process. Started and stopped by run.sh.
#
#   app_active / app_idle   connections the API holds open as tripsagent_app, by state. The API's
#                           Npgsql pool allows 100; PostgreSQL allows 100 in total.
#   waiting_on_lock         app connections waiting on a lock
#   redis_hits / misses     Redis keyspace counters since FLUSHALL. These count every Redis read —
#                           the rate limiter's and the pricing rules' too — so the search cache's own
#                           hit rate comes from k6's search_served_from_cache instead.
set -uo pipefail

API_PID="$1"
PG=trips-loadtest-postgres-1
REDIS=trips-loadtest-redis-1

echo "time,app_active,app_idle,app_total,waiting_on_lock,all_connections,redis_hits,redis_misses,redis_used_memory_bytes,redis_clients,api_cpu_percent,api_rss_kb"

while true; do
  pg=$(docker exec "$PG" psql -U trips -d trips_loadtest -Atc \
    "select count(*) filter (where usename='tripsagent_app' and state='active'),
            count(*) filter (where usename='tripsagent_app' and state like 'idle%'),
            count(*) filter (where usename='tripsagent_app'),
            count(*) filter (where usename='tripsagent_app' and wait_event_type='Lock'),
            count(*)
       from pg_stat_activity" 2>/dev/null | tr '|' ',')
  redis=$(docker exec "$REDIS" redis-cli INFO 2>/dev/null | tr -d '\r' | awk -F: '
    $1=="keyspace_hits"{h=$2} $1=="keyspace_misses"{m=$2} $1=="used_memory"{u=$2} $1=="connected_clients"{c=$2}
    END{printf "%s,%s,%s,%s", h, m, u, c}')
  api=$(ps -o %cpu=,rss= -p "$API_PID" 2>/dev/null | awk '{printf "%s,%s", $1, $2}')
  echo "$(date +%H:%M:%S),${pg:-,,,,},${redis:-,,,},${api:-,}"
  sleep 2
done
