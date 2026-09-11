# Runbook: the data retention job

The `data-retention` recurring job (Worker, Hangfire, 03:10 UTC daily) applies
[DATA_RETENTION.md](../DATA_RETENTION.md): it deletes operational rows past their window, clears
passport details after a trip, and reports on the supplier call log. It is a **dry run** until
`DataRetention__DryRun=false` is set on the Worker.

## Symptom

One of:

- The job is red in the Hangfire dashboard (`/hangfire` → Recurring jobs → `data-retention`).
- A `Data retention run … failed for …` error in the Worker's logs.
- Tables that should stay small — `identity.login_attempts`, `platform.outbox_messages` — keep
  growing, which means the job is not running, or is still in dry-run mode.
- Someone reports data missing that should have been kept. **Treat this as an incident at once.**

## How to confirm it

What the job did, or proposes to do, run by run:

```sql
SELECT occurred_at, correlation_id AS run, action, entity_id AS "table",
       after_state->>'rows' AS rows, after_state->>'window' AS "window",
       after_state->>'cutoff' AS cutoff, after_state->>'error' AS error
  FROM platform.audit_logs
 WHERE entity_type = 'DataRetention'
 ORDER BY occurred_at DESC
 LIMIT 50;
```

- `retention.dry_run` rows: the job is healthy and in dry-run mode. `rows` is what it would delete.
- `retention.failed`: that table failed; `error` says why. Every other table in the run still ran.
- No rows at all for today: the job did not run. Check the Worker is up and the recurring job exists.

## Impact

- **Failed or not running:** low urgency. Nothing is lost; data is simply kept longer than the
  schedule says. Fix it in working hours.
- **Data missing that should have been kept:** high. Deletions cannot be undone. Stop the job
  first (below), then work out what went.

## Fix

**The job failed for one table.** Read `error` in its `retention.failed` row.

1. A timeout: the first live run on a large backlog. Re-run it from the dashboard. It is
   idempotent, so a re-run only finishes what the first one started.
2. A foreign-key error: a new table references the one being purged. Classify the new table in
   `RetentionCatalogue` and make the rule respect the reference — do not delete around it by hand.
3. `Refusing to run`: a rule targets a table the catalogue does not allow. That is the guard
   working. Nothing was deleted. Fix the rule in code, not in the database.

**Stop the job at once** (suspected over-deletion):

1. Set `DataRetention__DryRun=true` on the Worker and restart it. The next run only counts.
2. To stop it running at all: Hangfire dashboard → Recurring jobs → `data-retention` → Delete.
   The Worker re-adds it on its next start, so do step 1 as well.
3. Find what went: the `retention.deleted` and `retention.anonymised` rows give each table, how many
   rows and the cutoff. Restoring means a database backup from before the run — escalate.

## Switching from dry run to live

Only after reading the dry-run rows for **several days in a row**, and only once the schedule has
been reviewed (see the status note at the top of [DATA_RETENTION.md](../DATA_RETENTION.md)).

1. For each table, check `rows` is plausible. A dry-run count close to the whole table is a wrong
   `WHERE` clause, not a clean-up.
2. Take a database backup and confirm it restores.
3. Set `DataRetention__DryRun=false` on the Worker and restart it.
4. Next morning, check the `retention.deleted` rows match the last dry run's counts, give or take a
   day's new rows.

`supplier.supplier_api_calls` is already live whatever this setting says: its partitions are
dropped by `supplier-api-call-maintenance`, which predates this job.

## If that doesn't work

Escalate to the backend lead. For anything involving deleted data, also the person who owns
the database backups.

## Prevention

- The protected-table guard and its tests (`RetentionCatalogueTests`, `DataRetentionPurgeTests`) are
  what stop this job reaching financial or audit records. Never delete or weaken them to make a
  build pass.
- Issue [#105](https://github.com/innovateavitech/trips-agent/issues/105).
