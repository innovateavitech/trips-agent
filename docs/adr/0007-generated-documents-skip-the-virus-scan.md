# ADR-0007: Serve the documents we render without a virus scan, and nothing else

**Status:** Proposed
**Date:** 2026-09-11
**Deciders:** Proposed with the branded invoices and vouchers (#46); for the repo owner to accept.

## Context

Issue #18 made "nothing unscanned is served" a rule. Every asset starts `Pending`, only the
Worker's pipeline can mark it `Clean`, `Asset.IsServable` needs `Ready` *and* `Clean`, and a CHECK
constraint, `ck_assets_ready_only_when_clean`, holds the database to the same line.

Issue #46 asks for every issued invoice and voucher to be "stored as an asset". Those PDFs are not
uploads. The Worker draws them itself, with QuestPDF, from rows already in our own database, and
writes them straight to a storage key that no upload URL was ever issued for.

Putting them through the scanner would check nothing a stranger sent us, because no stranger sent
anything. And no production scanner has been chosen yet: outside Development the asset pipeline
is switched off (`AssetPipelineStatus`), so every invoice would stay unservable until one is.

## Decision

A PDF the platform rendered is recorded as an asset with purpose `GeneratedDocument` and scan
status `NotRequired`, and is servable from the moment it exists. That pairing is the only
exception to the scan rule, and it holds in both directions: a generated document is never
scanned, and nothing else may ever be marked as not needing a scan.

## Options considered

### Option A — a purpose of its own and a "not required" scan status (chosen)

- ➕ Issued documents live with every other file an agency owns: one table for storage keys,
  retention and deletion requests.
- ➕ The row tells the truth about what happened — "we made it" — rather than claiming a scan
  that never ran.
- ➖ It widens the definition of a security control, so the exception has to be fenced in at
  every layer.

### Option B — scan them anyway

- ➕ No exception to the rule.
- ➖ Scanning our own renderer's output checks nothing.
- ➖ With no production scanner chosen, no invoice could ever be downloaded.

### Option C — keep the PDF off the assets table, on the document's own row

- ➕ The asset pipeline is untouched.
- ➖ Two places that hold an agency's files, which retention and deletion requests would both
  have to know about.
- ➖ Not "stored as an asset", which is what #46 asks for.

## Why we chose what we chose

The exception is only safe while it stays exactly this narrow, so each layer holds it:

- **Domain.** Only `Asset.RecordGenerated` sets `NotRequired`, and it sets the purpose with it.
  `Asset.Reserve` refuses `GeneratedDocument`, so nothing can be uploaded under that purpose.
- **Application and API.** `AssetRules.IsUploadable` excludes it. The upload endpoint refuses it,
  and the upload-limits list leaves it out.
- **Serving.** `Asset.IsServable` names both the purpose and the status, so neither is enough on
  its own.
- **Database.** `ck_assets_ready_only_when_clean` allows `Ready` without `Clean` only for that
  pairing, and `ck_assets_only_generated_documents_skip_the_scan` refuses `NotRequired` on any
  other purpose — so not even a hand-written `UPDATE` can promote an upload past the scanner.

## Consequences

### What this makes easier

- An invoice or voucher can be downloaded the moment it is rendered, whatever happens with the
  scanner decision.
- Every file an agency owns is still in one table.

### What this makes harder

- Anything that ever lets a person supply a "generated" document — an agency uploading its own
  voucher design, say — must not reuse this purpose, or it would skip the scanner. Give it a new
  purpose, scanned like any other upload.
- The rule from #18 now reads "nothing unscanned is served, except what the platform rendered
  itself". That asterisk has to be remembered wherever the rule is quoted.
