<!--
  Keep this PR under ~400 changed lines if you can. Small PRs get real reviews.
  PR title must be a conventional commit, e.g. feat(wallet): credit wallet on top-up
  It becomes the commit message on main when this is squash-merged.
-->

## What

<!-- One or two sentences. What does this change do? -->

## Why

<!-- The important bit. The code shows what changed; only you can explain the reason.
     Link the issue if there is one: Closes #123 -->

## How to test

<!-- Numbered steps a reviewer can actually follow. Include test data / accounts. -->

1.
2.
3.

## Screenshots

<!-- Required for any UI change. Before and after if you changed something existing. -->

## Notes for the reviewer

<!-- Anything you are unsure about, deliberately left out, or want a second opinion on.
     Saying "I wasn't sure about X" is a strength, not a weakness. -->

---

## Definition of Done

- [ ] This PR does **one** thing
- [ ] Tests cover the new behaviour, and existing tests pass
- [ ] I ran it locally and saw it work
- [ ] `pnpm verify` passes
- [ ] No commented-out code, no `Console.WriteLine` / `console.log`
- [ ] No secrets committed; new config added to `.env.example` with a dummy value
- [ ] New tenant-scoped tables have `agency_id` + a tenant filter
- [ ] Money uses minor units (`bigint`, `*_minor` column)
- [ ] Nothing traveller-facing hard-codes the Trips brand
- [ ] README / ADR updated if a decision changed
