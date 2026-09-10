# Epic breakdowns

Sixteen of the issues in [the backlog](../BACKLOG.md) carry the `epic` label — **#56 through
#71**. They are too big to pick up and finish in one pull request, and each says so in its own
body:

> This is an **epic**. Break it into individual issues first — aim for pieces that are each a day
> or two of work, and post the breakdown as a comment here before writing code.

This folder holds those breakdowns.

---

## Why a file, and not only a comment

The comment on the epic is what the instruction asks for, and it stays the thing people read
first. The file exists because a breakdown is a **design decision** — why this cut and not
another, what is blocked, what nobody owns yet — and a decision that lives only in a comment
thread is very hard to find six months later, when someone asks why the security work was split
the way it was.

A file also gets reviewed the way every other change here gets reviewed: through a pull request,
by a second person, before the child issues exist.

---

## The convention

- One file per epic, named `<issue number>-<short slug>.md`
- It proposes the child issues **in full** — title, body, acceptance criteria, dependencies — so
  creating them afterwards is copy-and-paste rather than a second round of thinking
- It ends with what the breakdown *found*: gaps with no owner, blocked items, and any of the
  [27 open questions](../ARCHITECTURE_AND_DELIVERY_PLAN.md) it touches
- The breakdown is posted as a comment on the epic; the child issues are created only once the
  breakdown has been agreed

After the child issues exist, regenerate the map:

```bash
./scripts/generate-backlog.sh
```

> That script arrives with [#76](https://github.com/innovateavitech/trips-agent/pull/76). If it is
> not on your branch yet, that pull request has not merged.

---

## Index

| Epic | Module | Milestone | Breakdown |
|---|---|---|---|
| [#59](https://github.com/innovateavitech/trips-agent/issues/59) | Storefront | M2 | [Custom domains, DNS verification and SSL](0059-custom-domains-dns-ssl.md) |
| [#60](https://github.com/innovateavitech/trips-agent/issues/60) | Storefront | M2 | [Public storefront rendering](0060-public-storefront-rendering.md) |
| [#62](https://github.com/innovateavitech/trips-agent/issues/62) | CRM | M2 | [Leads, quotes and pipeline](0062-crm-leads-quotes-pipeline.md) |
| [#68](https://github.com/innovateavitech/trips-agent/issues/68) | Analytics & Reporting | M3 | [Reporting and exports](0068-reporting-and-exports.md) |
| [#69](https://github.com/innovateavitech/trips-agent/issues/69) | Payments | M3 | [Payouts, disputes and gateway reconciliation](0069-payouts-disputes-reconciliation.md) |
| [#71](https://github.com/innovateavitech/trips-agent/issues/71) | Security | M3 | [Security hardening and load testing](0071-security-hardening.md) |

The other ten epics have not been broken down yet.
