# ADR-0001: PostgreSQL as the database

**Status:** Accepted
**Date:** 2026-09-10
**Deciders:** Tech lead

## Context

The backend is C# / .NET, where SQL Server is the conventional default. But the hosting cloud is
**not yet chosen** — AWS and Azure are both live options — and the choice of database would
otherwise quietly make that decision for us.

Four requirements shaped the choice:

1. **A tree of agencies.** Principal agents have sub-agents beneath them, and a principal must be
   able to query their whole subtree efficiently (for consolidated reporting).
2. **Strict tenant isolation.** One agency reading another's customers or prices is the worst bug
   this system could ship. We want a backstop below the application layer.
3. **High-volume append-only audit tables.** `supplier_api_calls` and `audit_logs` will grow
   quickly, and old data must be cheap to drop.
4. **Cost.** Our customers are small Nigerian travel agencies. Per-core licensing costs would come
   straight out of the margin on a low-priced subscription.

## Decision

**We use PostgreSQL 16, accessed through EF Core with the Npgsql provider.**

## Options considered

### Option A — SQL Server

- ➕ The conventional .NET choice; best tooling and EF Core support
- ➕ Team familiarity is highest here
- ➖ Licensing cost per core, on a product with thin per-tenant margins
- ➖ Effectively commits us to Azure
- ➖ No native tree type; the hierarchy needs `hierarchyid` (awkward in EF Core) or a closure table
- ➖ Row-level security exists but is less commonly used and less well documented

### Option B — PostgreSQL *(chosen)*

- ➕ Free, and first-class on both AWS (RDS/Aurora) and Azure (Flexible Server)
- ➕ `ltree` gives indexed subtree queries for the agency hierarchy
- ➕ Row-Level Security is mature and widely used as a tenancy backstop
- ➕ Declarative table partitioning makes dropping old audit data a metadata operation
- ➕ `jsonb` for supplier payloads we must store verbatim but rarely query
- ➖ Slightly less familiar to a .NET team
- ➖ EF Core support is excellent but marginally behind SQL Server on edge cases

### Option C — MySQL

- ➖ Weakest of the three on the specific features we need: no `ltree`, no RLS, weaker `jsonb`

## Why we chose what we chose

Postgres wins on the four requirements *and* on the one thing we cannot yet decide.

The deciding factor is that it keeps the cloud choice open. Picking SQL Server today would mean
choosing Azure today, months before we have the information to choose well. Postgres lets that
decision wait until it can be made on its merits.

The feature fit is not incidental either — `ltree`, RLS and partitioning each map directly onto a
requirement we know we have, rather than being nice-to-haves we might grow into.

## Consequences

### What this makes easier

- Deploy to AWS or Azure without a rewrite
- The agency hierarchy is one indexed query, not a recursive CTE or a closure table
- Tenant isolation gets a database-level backstop under the application filters
- Dropping a month of `supplier_api_calls` is instant

### What this makes harder

- A learning curve for developers who only know SQL Server: `citext`, `jsonb`, `ltree` and the
  `\d` psql commands are all new
- Some EF Core patterns need Npgsql-specific configuration
- RLS means connections must set a tenant GUC per request, which is extra infrastructure that must
  be correct or isolation silently degrades

### What we would need to see to revisit this

A cloud decision that makes Azure SQL materially cheaper in total cost than self-managed
Postgres, or a Postgres-specific limitation we cannot work around.
