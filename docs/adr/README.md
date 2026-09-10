# Architecture Decision Records

An ADR is a short note recording **one significant decision**: what we chose, why, and what it
costs us.

## Why bother

Six months from now someone — quite possibly you — will look at a choice in this codebase and
think *"why on earth did they do it that way?"* Without an ADR, the honest answer is nobody
remembers, so the decision gets quietly reversed, the original problem comes back, and the team
loses a week rediscovering it.

An ADR is also permission to change your mind. Writing the reasoning down means a future
decision can supersede this one **deliberately**, with everyone understanding what is being
traded away.

## When to write one

Write an ADR when a choice is **hard to reverse** or **will surprise someone later**:

- Choosing a database, queue, or major library
- Choosing an architectural pattern (why multi-tenancy works the way it does)
- Choosing a process rule everyone has to follow (why we squash-merge)
- Deliberately *not* doing something obvious (why we don't retry the ticket-issue call)

Do **not** write one for: naming a variable, picking a colour, or anything you could undo in an
afternoon.

## How

1. Copy `template.md` to `NNNN-short-title.md` — next number, kebab-case title
2. Fill it in. Aim for **one page**. If it takes three, the decision probably needs splitting
3. Open a PR. The discussion happens in review
4. Once merged, the ADR is **immutable**. To change the decision, write a new ADR that
   supersedes it and update the old one's status to `Superseded by ADR-NNNN`

## Status values

| Status | Meaning |
|---|---|
| `Proposed` | Under discussion in a PR |
| `Accepted` | This is what we do |
| `Superseded by ADR-NNNN` | We changed our minds. The new one explains why |
| `Deprecated` | No longer relevant — the thing it decided about is gone |

## Index

| # | Decision | Status |
|---|---|---|
| [0001](0001-postgresql-as-the-database.md) | PostgreSQL as the database | Accepted |
| [0002](0002-protected-main-and-squash-merge.md) | Protected `main` and squash-merge only | Accepted |
| [0003](0003-never-retry-ticket-issuance.md) | Never retry the supplier's ticket-issue call | Accepted |
| [0004](0004-masstransit-v8-and-hangfire.md) | Pin MassTransit to v8, and split cron from events | Proposed |
