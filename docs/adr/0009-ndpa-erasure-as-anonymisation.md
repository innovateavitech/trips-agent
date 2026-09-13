# ADR-0009: NDPA erasure is anonymisation, with the financial record kept

**Status:** Accepted for the MVP, pending the client's DPO sign-off
**Date:** 2026-09-13
**Deciders:** Engineering, under the build plan's decision 26. **Not yet reviewed by Nigerian
counsel or by the client's Data Protection Officer** — see *Who approved this* below.

## Context

Two duties point in opposite directions.

**The NDPA 2023 gives a person the right to erasure.** Section 34 lets a data subject ask for their
personal data to be deleted, and we have to be able to do it and to show that we did.

**Other law requires us to keep records that contain their personal data.** An invoice is a tax
record; a wallet statement and a ledger entry explain where money went; an audit row is the answer
to "who changed this, and when". The plan gives all of them seven years
([DATA_RETENTION.md](../DATA_RETENTION.md)), and the ledger is append-only in the database itself —
the application role has no `DELETE` on it at all (ADR-0006).

Deleting a traveller's row outright would therefore do three things, all bad:

1. Break the link between an order and who bought it, so the agency's own sales record stops
   explaining itself.
2. Leave the ledger balanced but unexplainable: the money is there, the reason is not.
3. Delete somebody *else's* record too — an order is the agency's business record as much as it is
   the traveller's personal data.

The NDPA anticipates this. The right to erasure is not absolute: it does not apply where processing
is necessary for compliance with a legal obligation, or for the establishment or defence of a legal
claim. What it does not excuse is keeping the person's *identity* attached to records kept for those
reasons when the records do not need it.

## Decision

**Erasure means anonymisation in place. The record keeps its shape; it stops identifying anyone.**

Concretely, one request (`platform.erasure_requests`, `CustomerErasureService`):

- **The person's details are replaced, not the rows.** A name becomes `Erased at request`, a
  traveller becomes `Erased Traveller`, a birth date, a nationality, a phone number and a gender
  become null. An email becomes a unique address in the reserved `.invalid` domain, rather than
  null, because `crm.customers` requires a customer to be contactable and the alternative would be
  deleting the row.
- **Travel documents are destroyed, not anonymised.** `supplier.passenger_documents` rows are
  deleted outright, and the encrypted passport number and expiry on `orders.order_travellers` are
  cleared. A document number anonymised is a document number kept.
- **Uploaded files are deleted from blob storage through `IBlobStorage`.** A chargeback's evidence
  files go; the asset row stays, marked `Erased`, so the trail still shows a file was there and is
  not.
- **Financial and audit records survive, untouched.** Ledger entries, ledger accounts, wallets,
  order lines and their frozen prices, payments, refunds, disputes and audit rows are not written to
  at all. The nightly ledger integrity audit passes afterwards, and a test proves it.
- **The record of the erasure survives too**, and holds no personal detail: who asked, when, for
  which customer, on what stated reason, and how many rows changed in which tables.
- **It is irreversible.** Nothing is copied anywhere first: no archive table, no export, no
  "erased customers" file. The only way back is a database backup, and that is a property of
  backups (see *What this does not do*).
- **It is refused rather than half-done** while an order is still being paid for or fulfilled, or
  a chargeback is still open. Those need the person's details to finish, they resolve in days, and
  the refusal is recorded with its reason exactly like a completed erasure.

**Who may run it:** `platform.erasure.execute`, held only by a Super Admin. The preview is behind
the same permission: looking somebody up by email across every agency is itself a read no support
account should make.

## What this deliberately leaves behind

Honest list, because "erased" must not mean "mostly erased":

- **The bytes of an invoice or voucher that was already issued.** The file is the tax record as it
  was sent, and the database refuses to change it (ADR from #46's guard). The *recipient on the row*
  is anonymised — that is what a search finds, and what a reissue would otherwise print again — and
  a document rendered after an erasure prints the placeholder. A test proves that too.
- **The audit log.** Rows written before the erasure may contain the name or email that was
  changed. The audit log is append-only by trigger and is the record of who did what; erasing it
  would erase the evidence of the erasure itself. Its own retention (84 months, then whole
  partitions dropped) is what eventually removes them.
- **Database backups, and anything the hosting platform holds.** A restore brings back what the
  backup held. Backup retention is set by the hosting platform, which has not been chosen
  ([DATA_RETENTION.md](../DATA_RETENTION.md) says so under *Not covered yet*).
- **The agency's staff accounts.** `identity.users` is out of scope: an agency's employee is not a
  traveller, and their account ends by being closed rather than erased.
- **What an agent wrote in the CRM timeline.** `crm.communications` holds an agent's own summary of
  a conversation, pointing at the customer rather than repeating their details; the details
  themselves live on `crm.customers` and are erased there. Free text that names somebody anyway is a
  known gap, listed here rather than quietly fixed with a text search.

## Alternatives considered

**Delete the person's rows outright.** Rejected: it breaks the ledger's explanation, deletes the
agency's own business record, and collides with the seven-year retention that tax law imposes on us
directly. It also cannot be done: the ledger and the audit log refuse `DELETE` at the database.

**Do nothing until counsel answers.** Rejected for the MVP, but only because of *how* this is
built. Nothing here needs a legal opinion to be safe: everything it destroys is data nobody claims
we must keep (travel documents, contact details, uploaded evidence), and everything it keeps is data
somebody plainly does (money, tax, audit). If counsel's reading is stricter, what changes is the
list of columns, not the mechanism.

**Deterministic pseudonyms — replacing the name with a stable hash** so the same person could still
be recognised across records. Rejected: a stable pseudonym plus an order history is re-identifiable,
and being able to recognise somebody is exactly what they asked us to stop doing.

## Consequences

- ➕ A person can be erased without the books, the bookings or the paperwork breaking.
- ➕ The erasure is recorded and auditable, which is half of what the NDPA asks for.
- ➕ Financial records keep their seven years, so a tax authority's question can still be answered.
- ➖ The residue above is real, and some of it (issued PDFs, audit rows, backups) can only be
  removed by time.
- ➖ Erasure is a platform operation, not something an agency can do for itself. That is deliberate
  for the MVP — it is irreversible, and a Trips Super Admin is the smallest set of people who can do
  it — and it means an agency has to ask us.
- ➖ Anonymising a customer loses the agency a repeat customer's history. They are told so on the
  screen before they ask for it.

## Who approved this

**This is an engineering decision taken under the build plan, not a legal opinion.** The build
plan's *Decisions for the MVP* answers open question 26 this way so that work does not wait, and
[DATA_RETENTION.md](../DATA_RETENTION.md) records counsel's review of the retention schedule as
outstanding.

Before launch, the client's Data Protection Officer — or Nigerian counsel acting for them — should
confirm, in writing:

1. That anonymisation-with-preservation satisfies section 34 for records we are obliged to keep.
2. The list under *What this deliberately leaves behind*, particularly the issued invoice PDFs and
   the audit log.
3. The seven-year retention itself, which is the other half of the same question.

When that confirmation arrives, this ADR's status becomes **Accepted**, with the name and date of
whoever gave it. Until then it says exactly what it is: the plan's own recommendation, built, tested
and written down so it can be reviewed rather than guessed at.

## References

- Issue [#106](https://github.com/innovateavitech/trips-agent/issues/106) — the acceptance criteria
- [docs/BUILD_PLAN.md](../BUILD_PLAN.md) — decision 26
- [docs/DATA_RETENTION.md](../DATA_RETENTION.md) — what is kept, and for how long
- [ADR-0006](0006-row-level-security-backstop.md) — why the application cannot delete from the ledger
- `backend/services/TripsAgent.Application/Platform/CustomerErasureService.cs` — the implementation
