#!/usr/bin/env bash
# Boils sample.sh's CSV down to the peaks the report quotes.
set -euo pipefail

awk -F, 'NR==1 { next }
  $2 == "" { next }
  {
    n++
    if ($2 > active) active = $2
    if ($4 > total) total = $4
    if ($5 > locks) locks = $5
    if ($6 > all) all = $6
    if ($9 > mem) mem = $9
    if ($11 > cpu) cpu = $11
    if ($12 > rss) rss = $12
    sum_active += $2
    hits = $7; misses = $8
  }
  END {
    printf "samples: %d (every 2 s)\n", n
    printf "postgres: app connections open, peak %d of a 100-connection pool (active peak %d, mean %.1f); all connections peak %d of max_connections 100; waiting on a lock, peak %d\n", total, active, (n ? sum_active / n : 0), all, locks
    printf "redis: keyspace hits %d, misses %d (every Redis read, not only the search cache); memory peak %.1f MB\n", hits, misses, mem / 1048576
    printf "api process: cpu peak %.0f%% (100%% = one core), memory peak %.0f MB\n", cpu, rss / 1024
  }' "$1"
