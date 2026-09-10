# Runbook: platform audit log

`platform.audit_logs` records who changed what, when, and what it looked like before and after.
It is append-only and partitioned by month. This page is for whoever operates the database.

## What keeps it running

**One partition per calendar month must exist before that month starts.** Without a partition
covering the current instant, every insert fails — and because audit rows are written inside the
same transaction as the change they describe, _every audited save in the application fails with
it_.

The migration creates last month through three months ahead. After that, the Worker keeps it
moving: the `audit-log-maintenance` Hangfire recurring job runs daily at 03:00 UTC, creating the
upcoming partitions and dropping the ones past the retention window. Both halves are safe to
repeat.

To run it by hand — after restoring a backup, or to see a failure in your own terminal:

```bash
cd backend
dotnet run --project services/TripsAgent.Api -- audit-maintenance
```

It exits 0 on success and 1 on failure.

## Retention

Set in configuration, not in SQL:

```json
"AuditLog": { "RetentionMonths": 84, "PartitionsCreatedAhead": 3 }
```

**84 months is a placeholder, not a decision.** Open question 26 in the
[delivery plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) weighs a seven-year hold on financial and
audit records against the NDPA 2023 right to erasure, and says it needs Nigerian legal counsel.
When there is an answer, change the number — no migration and no code change needed. A value
below 1 is refused at startup.

Expired history leaves by dropping a whole month's partition. Individual rows are never deleted.

## Append-only

Two layers, because one is not enough:

| Layer                                         | Protects against                                                          | Status                                                      |
| --------------------------------------------- | ------------------------------------------------------------------------- | ----------------------------------------------------------- |
| Trigger `audit_logs_append_only`              | Any `UPDATE` or `DELETE`, by any role, including via a partition directly | Always on                                                   |
| `REVOKE UPDATE, DELETE … FROM tripsagent_app` | The application role, at the privilege level                              | Applied **only if the role exists** when the migration runs |

The trigger is what protects the table today. The REVOKE cannot stand alone: a table's owner keeps
its privileges regardless, and the application currently connects as the owner.

This protects the table from the application, not from a database administrator: an owner or
superuser can still `ALTER TABLE … DISABLE TRIGGER`. That is what the two-person rule below is for.

**When the application role is provisioned**, run this once, then again after any future
migration that recreates the table:

```sql
REVOKE UPDATE, DELETE ON platform.audit_logs FROM tripsagent_app;
```

## When something goes wrong

**Inserts failing with `no partition of relation "audit_logs" found for row`**
The recurring job has not been running. Run `audit-maintenance` by hand now, then look at the
`audit-log-maintenance` job's history in the Hangfire dashboard (`/hangfire` on the API, when
enabled) to find out why. Three months of runway means this took a quarter of silence to reach.

**Someone needs to correct a wrong audit row**
You cannot, by design. Write a new audit entry that explains the correction and references the
original row's `id`.

**A partition must be dropped early** — for example, under a legal instruction
This is DDL and bypasses the trigger. It needs a written instruction and a second person:

```sql
DROP TABLE platform.audit_logs_2026_09;
```
