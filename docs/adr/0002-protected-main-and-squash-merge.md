# ADR-0002: Protected `main` and squash-merge only

**Status:** Accepted
**Date:** 2026-09-10
**Deciders:** Repository owner

## Context

The team building this platform is **early in their careers**. That is a design constraint on the
process, not just the code.

Git is the most common place for a new developer to do accidental damage, and the damage is
disproportionate to the mistake: a force-push to `main` that erases a day of someone else's work,
an untested commit that breaks everyone's build, a secret pushed to a public repository, a merge
history so tangled that nobody can work out what changed when.

None of these come from carelessness. They come from not yet knowing which git operations are
dangerous — which is exactly what being new means.

The system also handles real money: agent wallets, a double-entry ledger, live airline tickets. A
bad change reaching `main` unreviewed is not a cosmetic problem.

## Decision

**`main` is protected. Nobody pushes to it directly. Every change arrives through a pull request
with at least one approval and passing CI. Pull requests are merged by squash only.**

The repository owner keeps an admin bypass (`enforce_admins: false`) as an emergency escape
hatch.

## Options considered

### Option A — Trust and convention

- ➕ No setup, no friction
- ➖ Relies on everyone always remembering the rules, including at 6pm on a Friday
- ➖ Some mistakes (force-push to `main`) are extremely hard to recover from
- ➖ Puts the tech lead in the position of policing rather than reviewing

### Option B — Protected `main`, all merge types allowed

- ➕ Prevents direct pushes
- ➖ `main`'s history fills with merge commits and thirty-commit branches called "wip"
- ➖ Reverting a change means untangling which commits belonged to it

### Option C — Protected `main`, squash-merge only *(chosen)*

- ➕ Everything above, plus one clean commit per change on `main`
- ➕ Reverting is a single safe operation
- ➖ Loses fine-grained branch history after merge
- ➖ Nobody can push a quick fix without a PR

### Option D — Option C with no admin exception at all

- ➕ Genuinely no exceptions; the rule is the rule
- ➖ No escape hatch if CI itself breaks and blocks all merges

## Why we chose what we chose

**Protection over convention** because the rules that matter are the ones that hold when someone
is tired, rushed, or does not yet know what is dangerous. A rule enforced by GitHub is a rule; a
rule in a document is a hope.

**Squash-merge is the highest-leverage single setting for this team.** It removes the pressure to
have a tidy branch history, which is a real source of anxiety for people still learning git.
Commit as often and as messily as you like — it all becomes one clean commit on `main`. It also
makes `git log` on `main` genuinely readable, and makes reverting trivial.

**An admin bypass, held by one person**, because CI can break in ways that block every merge, and
there needs to be a way out. It is expected to be used approximately never.

## Consequences

### What this makes easier

- `main` is always reviewed, always CI-green, always deployable
- Every change gets a second pair of eyes — which for a team that is learning is as much about
  teaching as catching bugs
- Reverting is one command
- Nobody can destroy `main`'s history
- Developers are free to commit messily while working, which is how people actually work

### What this makes harder

- Every change needs a reviewer, so a blocked colleague is now a real cost. `CONTRIBUTING.md`
  asks for reviews within one working day
- Fine-grained history is lost after merge. Acceptable — the PR keeps the full discussion
- A single-person bottleneck exists on payments, migrations, tenancy and supplier code via
  `CODEOWNERS`. Deliberate for now; revisit as the team gains experience

### How this is enforced

- GitHub branch protection on `main` — see [`docs/BRANCH_PROTECTION.md`](../BRANCH_PROTECTION.md)
- Merge commits and rebase-merging disabled in repository settings
- Only the repository owner holds the Admin role; everyone else has Write
- `CODEOWNERS` routes money-path and schema changes to the owner automatically

### What we would need to see to revisit this

The team gaining enough experience that the review bottleneck costs more than it teaches — at
which point we widen `CODEOWNERS` rather than weaken the protection. Two required approvals also
becomes reasonable once the team is larger.
