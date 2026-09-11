# ADR-0006: Enforce tenant isolation in PostgreSQL too, with row-level security

**Status:** Accepted
**Date:** 2026-09-11
**Deciders:** Platform team (issue #12)

## Context

Tenant isolation lives in EF Core query filters: every agency-owned entity is filtered by
`agency_id`, and analyser TRIPS002 fails the build if production code calls
`IgnoreQueryFilters()`. That is one layer. A raw SQL query, a filter missed on a new entity, or a
bug in the filter itself would read every agency's rows, and nothing below the application would
notice. In a white-label product that is one travel agency seeing another's customers and prices.

Two facts shaped the design. **PostgreSQL skips every policy for a superuser or a BYPASSRLS role,
and for a table's owner unless the table is `FORCE`d.** Until this change the application connected
as the superuser docker creates, so a policy would have enforced nothing — and the existing
`REVOKE ... FROM tripsagent_app` on the append-only tables were no-ops, because that role did not exist.
And **EF runs most commands in autocommit**, so a transaction-local `set_config(..., true)` sent as
its own statement expires before the query it was meant for.

## Decision

PostgreSQL row-level security on every agency-owned table, mirroring the EF filters exactly, with
the application connecting as a role the policies bind.

- `tripsagent_app` — `NOSUPERUSER NOBYPASSRLS` — is the runtime role. Migrations and DDL use a
  separate owner connection (`ConnectionStrings:PostgresAdmin`), which must be a superuser or
  `BYPASSRLS`.
- `TenantSessionInterceptor` writes `app.agency_id` and `app.platform_scope` at **session** level on
  every connection open, re-checks them before every command, and treats settings written inside a
  transaction as provisional.
- Policies read them through `tenancy.current_agency_id()` and `tenancy.platform_scope_active()`.
  Unset means no tenant and no scope, which matches nothing: they fail closed.
- Every policed table is `FORCE ROW LEVEL SECURITY`, so connecting as the owner by mistake is still
  policed.
- Triggers that read rows to check integrity (the ledger balance check, the agency path triggers)
  run `SECURITY DEFINER`, so they see the whole table rather than the caller's tenant.

## Options considered

### Option A — EF filters only (the status quo)

- ➕ Nothing new to operate
- ➖ One layer. Any path around EF — raw SQL, a missed filter — crosses tenants silently

### Option B — A separate schema or database per agency

- ➕ The strongest isolation there is
- ➖ Migrations run once per agency, cross-agency platform reporting becomes a federation problem,
  and the sub-agent hierarchy spans schemas. Wrong scale for this product's stage

### Option C — Row-level security behind the filters (chosen)

- ➕ A second, independent layer, enforced below the application
- ➖ A second role to provision, and a round trip to set the tenant on every connection open

## Why we chose what we chose

It keeps one schema and one migration path while making "the filter was missing" a non-event
rather than a breach. The policies deliberately mirror the filters, so nothing that works today
changes behaviour — the test suite runs the money path and the API host as `tripsagent_app` to
prove it.

**What this does not buy.** It is a backstop against a missed filter, not a defence against SQL
injection: code that can run arbitrary SQL can set `app.platform_scope` as easily as the
interceptor does. The platform scope stays the only sanctioned route across tenants, and it is
logged.

## Consequences

### What this makes easier

- A new agency-owned table cannot quietly go unprotected: `RowLevelSecurityTests` fails the build
  until it has a policy or a written exemption.
- The append-only `REVOKE`s on the ledger and the audit log finally apply to the application.

### What this makes harder

- **Two connection strings.** Deployed environments need `ConnectionStrings:Postgres` as
  `tripsagent_app` and `ConnectionStrings:PostgresAdmin` as the owner. The migration creates the role
  `NOLOGIN`; set its password outside git: `ALTER ROLE tripsagent_app LOGIN PASSWORD '…'`.
- Local development still connects as the docker superuser by default, so RLS is not enforced there
  and `/health` reports **Degraded** saying so. To run as production does, provision the role as above
  and point `ConnectionStrings__Postgres` at it. The integration tests always do.
- Every new agency-owned table's migration needs `ENABLE` + `FORCE ROW LEVEL SECURITY` and a
  `tenant_isolation` policy, written like `AddRowLevelSecurity`'s.
- One extra statement per connection open.
- `/health` is **Unhealthy in Production** if the application's role can bypass RLS — deliberately,
  so a misconfigured deploy is pulled rather than served.

### Deliberately not policed

`platform.audit_logs` (its own filter on the audit actor; platform-wide rows), `platform.admin_alerts`
(written from agency requests, read by Trips staff across agencies), `platform.outbox_messages`
(infrastructure), and `payments.reconciliation_exceptions` (platform-only). The coverage test lists
each with its reason.

### What we would need to see to revisit this

A need for cross-agency queries that the platform scope cannot express, or measurable cost from the
per-connection round trip.
