# Contributing to the Trips Agent Platform

This guide assumes you have never worked on a shared team repository before. Every instruction
is a command you can copy and paste. If something here is unclear or wrong, fixing it is a
genuinely valuable pull request.

**The one rule that matters:** nobody pushes to `main`. Everything goes through a pull request.

---

## Table of contents

0. [Before anything else — run setup.sh](#0-before-anything-else)
1. [The golden rules](#1-the-golden-rules)
2. [How branching works here](#2-how-branching-works-here)
3. [Writing commit messages](#3-writing-commit-messages)
4. [The everyday workflow](#4-the-everyday-workflow)
5. [Opening a pull request](#5-opening-a-pull-request)
6. [Definition of Done](#6-definition-of-done)
7. [Code review](#7-code-review)
8. [When things go wrong](#8-when-things-go-wrong)
9. [Project-specific traps](#9-project-specific-traps)
10. [Getting help](#10-getting-help)

---

## 0. Before anything else

```bash
./scripts/setup.sh
```

Run this once after cloning. It installs the shared git hooks and checks your tooling. Safe to
re-run any time.

The hooks are the reason a mistake becomes a two-second message instead of a two-day problem:

| Hook | Stops you from |
|---|---|
| `pre-commit` | Committing on `main`, committing a secret, committing a huge binary |
| `commit-msg` | Writing a commit message that doesn't follow our format |
| `pre-push` | Pushing to `main` |

They live in [`.githooks/`](.githooks/) and are version-controlled, so everyone gets the same
ones. They are **not** installed automatically by `git clone` — that is what `setup.sh` is for.

**About `--no-verify`:** it skips the hooks. It exists for genuine false positives, such as a
secret-scanner hit on a string that only looks like a key. It is not a way around a hook that is
correctly telling you to branch first. If a hook blocks you and you do not understand why, ask —
do not reach for `--no-verify`.

---

## 1. The golden rules

0. **Run `./scripts/setup.sh` once after cloning.** Without it you have no safety nets.
1. **Never push to `main`.** The pre-push hook rejects it. This is a safety net, not a punishment.
2. **One branch per task.** Small and focused beats big and complete.
3. **Always start from an up-to-date `main`.**
4. **Run the tests before you push.** `pnpm verify` runs exactly what CI runs.
5. **Never commit secrets.** No API keys, no passwords, no `.env` file. If you do it by
   accident, see [§8](#8-when-things-go-wrong) — do not just delete and re-commit.
6. **Ask after 30 minutes of being stuck.** Every time. This is the rule, not an exception.

---

## 2. How branching works here

We use **trunk-based development**, which is a formal name for something simple:

```
main                              ← protected. always working. always deployable.
  ├─ feat/M1-wallet-topup         ← your branch. lives a few days at most.
  ├─ fix/login-lockout-counter
  └─ chore/upgrade-efcore
```

You branch off `main`, do your work, open a pull request, someone reviews it, and it merges
back into `main`. Then your branch is deleted. That is the whole model.

There is no `develop` branch, no `release` branch, no GitFlow. We deliberately chose the
simplest thing that works, because extra branches cause more confusion than they prevent.

### Naming your branch

```
<type>/<milestone-or-issue>-<short-description>
```

All lowercase, words separated by hyphens.

| Type | Use it for | Example |
|---|---|---|
| `feat` | A new feature | `feat/M1-wallet-topup` |
| `fix` | Fixing a bug | `fix/login-lockout-counter` |
| `docs` | Documentation only | `docs/readme-setup-steps` |
| `refactor` | Restructuring, no behaviour change | `refactor/extract-price-calculator` |
| `test` | Adding or fixing tests | `test/wallet-concurrency` |
| `chore` | Dependencies, config, tooling | `chore/upgrade-efcore` |

If there is a GitHub issue, use its number: `fix/142-duplicate-invoice-number`.

---

## 3. Writing commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/). The format:

```
<type>(<scope>): <subject>
```

**Good:**

```
feat(wallet): credit agent wallet on successful Paystack top-up
fix(auth): reset failed_login_count after a successful login
docs(readme): add local setup troubleshooting table
refactor(pricing): extract markup resolution into its own service
test(supplier): cover domestic confirm returning an array
chore(deps): upgrade EF Core to 9.0.2
```

**Not good:**

```
update stuff              ← update what? why?
fix                       ← fix what?
WIP                       ← say what you were working on
Fixed the bug where...    ← no type, wrong tense, too long
```

### The rules

- **Type** must be one of: `feat` `fix` `docs` `refactor` `test` `chore` `perf` `build` `ci`
- **Scope** is the area you touched: `auth` `wallet` `supplier` `catalog` `storefront` `admin`
  `crm` `ui` `db` `deps`
- **Subject** is lowercase, present tense ("add", not "added"), no full stop, under 72 characters
- Write what the change *does*, not what you did

A git hook checks this when you commit. If your message is malformed the commit is rejected
immediately, which is far kinder than finding out in a code review two days later.

### Why we bother

The commit history becomes a readable changelog, we generate release notes from it
automatically, and — most usefully — when something breaks in six months, `git log` actually
tells you what happened and why.

---

## 4. The everyday workflow

This is the loop you will run dozens of times. Learn it once.

> Using Claude Code to write the change? [`docs/WORKING_WITH_CLAUDE.md`](docs/WORKING_WITH_CLAUDE.md)
> covers the same loop with the AI steps included — how to brief it, and how to review what it
> produces before you put your name on it.

### Step 1 — Start fresh

```bash
git checkout main
git pull origin main
```

Always. Every time. Starting from a stale `main` is the number one cause of painful merge
conflicts.

### Step 2 — Create your branch

```bash
git checkout -b feat/M1-wallet-topup
```

### Step 3 — Do the work

Write the code. Write a test for it. Run the app and actually look at it.

### Step 4 — Check yourself before pushing

```bash
pnpm verify
```

This runs the linter, the formatter, the .NET tests and the front-end tests — everything CI
will run. Getting a green result here means your pull request will almost certainly pass.

### Step 5 — Review your own diff

```bash
git add -p
```

The `-p` flag walks you through your changes one chunk at a time and asks whether to stage each
one. It takes an extra minute and it catches an enormous amount: a stray `console.log`, a
commented-out block, a file you did not mean to touch.

Press `y` to stage a chunk, `n` to skip it, `?` for help.

### Step 6 — Commit

```bash
git commit -m "feat(wallet): credit agent wallet on successful top-up"
```

Commit as often as you like. Small commits are easier to undo. Because we squash-merge, your
messy history disappears when the PR merges.

### Step 7 — Push your branch

```bash
git push -u origin feat/M1-wallet-topup
```

The `-u` is only needed the first time. Afterwards, `git push` is enough.

### Step 8 — Open the pull request

```bash
gh pr create --fill
```

Or open the link GitHub prints in your terminal.

---

## 5. Opening a pull request

### Before you open it

- [ ] `pnpm verify` passes locally
- [ ] Your branch is up to date with `main`
- [ ] You have looked at your own diff on GitHub and are happy with it

### Writing the description

The template will prompt you. The most important field is **why** — the code shows what
changed; only you can explain the reason.

```markdown
## What
Credits the agent's wallet when a Paystack top-up succeeds.

## Why
Agents cannot book anything until their wallet is funded. This closes the last
gap in the top-up flow (issue #87).

## How to test
1. Log in as agent@demo.test
2. Wallet → Top Up → enter 50000
3. Pay with Paystack test card 4084 0840 8408 4081
4. Wallet balance should read ₦50,000 and a ledger entry should appear

## Notes
Wallet balance is a cached projection. The ledger is the source of truth, so
I added an assertion that they match after the credit.
```

### Keep it small

Aim for **under 400 changed lines**. A 200-line PR gets a careful review. A 2,000-line PR gets
an approval nobody actually read, which helps no one.

If a task is genuinely large, split it: schema first, then backend, then UI — three reviewable
PRs instead of one unreviewable one.

### What happens next

1. CI runs. All checks must pass.
2. GitHub requests a review from the relevant code owner automatically.
3. A reviewer comments or approves.
4. You address the comments — push more commits to the same branch, no need to start over.
5. Once approved and green, **squash and merge**.
6. Your branch is auto-deleted. Go back to step 1.

### Why "squash and merge"?

All your commits become one clean commit on `main`, using your PR title as its message. Your
branch can have thirty commits called "wip" and it will not matter. It also means reverting a
change later is a single, safe operation.

---

## 6. Definition of Done

A pull request is ready when:

- [ ] It does **one** thing
- [ ] Tests cover the new behaviour, and existing tests still pass
- [ ] You have run it locally and seen it work
- [ ] No commented-out code, no `Console.WriteLine`, no `console.log`
- [ ] No secrets. Any new config is added to `.env.example` with a dummy value
- [ ] New tenant-scoped tables have an `agency_id` column and a tenant filter
- [ ] Money is stored in minor units as `bigint`, in a `*_minor` column
- [ ] Any user-visible string is ready for the agent's branding — no hard-coded "Trips"
- [ ] The README or an ADR is updated if you changed a decision
- [ ] The PR description explains **why**

---

## 7. Code review

Reviews are how we catch problems and how we learn from each other. They are about the code,
never about the person who wrote it.

### If you wrote the code

- Keep it small. Under 400 lines.
- Explain **why** in the description.
- Respond to every comment, even if only with "good catch, fixed".
- Disagreeing is fine and encouraged — say why. Reviewers are often wrong.
- Do not take it personally. Everyone's code gets comments, including the lead's.

### If you are reviewing

- **Review within one working day.** A blocked colleague is more expensive than your context switch.
- **Say what is blocking and what is not.** Prefix anything optional with `nit:`:
  - `nit: could extract this into a helper — not blocking`
  - `This will throw if the wallet is null — needs fixing before merge`
- **Explain your reasoning.** "Use a lock here" teaches nothing. "Two people could book the last
  seat at the same time here — we need a lock so the capacity check and decrement happen
  together" teaches the concept.
- **Ask, do not command.** "What happens if the API times out here?" is better than "handle the timeout".
- **Approve when it is good enough.** Perfect is not the bar. Working, tested, and readable is.
- **Say what is good.** If someone handled a tricky case well, tell them. Reviews should not be
  purely a list of faults.

### Who reviews what

`CODEOWNERS` routes reviews automatically. Anything touching **payments, the ledger, database
migrations, or the supplier integration** goes to the team lead, because those are the places
where a mistake costs real money.

---

## 8. When things go wrong

Every one of these has happened to every developer alive. None of them is a disaster.

### "`main` moved while I was working"

Bring your branch up to date:

```bash
git checkout main
git pull origin main
git checkout feat/my-branch
git rebase main
```

If there is a conflict, git will stop and tell you which files. Open each one — you will see:

```
<<<<<<< HEAD
the version from main
=======
your version
>>>>>>> your commit
```

Delete the markers, keep the code that should win (sometimes both), then:

```bash
git add <the-file>
git rebase --continue
git push --force-with-lease
```

`--force-with-lease` is the safe version of `--force`: it refuses if someone else pushed to your
branch. **Never use plain `--force`, and never force-push to `main`.**

Lost and want out? `git rebase --abort` returns you exactly to where you started.

### "I accidentally committed to `main`"

The pre-commit hook normally stops this, so you'll only get here if the hooks weren't installed
(run `./scripts/setup.sh`) or you used `--no-verify`. Either way nothing is broken — the pre-push
hook will still refuse to send it. Move the commit onto a branch:

```bash
git branch feat/my-work        # save your commit onto a new branch
git reset --hard origin/main   # put main back to normal
git checkout feat/my-work      # carry on working
```

### "I committed the wrong file"

If you have not pushed yet:

```bash
git reset --soft HEAD~1   # undo the commit, keep the changes staged
git restore --staged path/to/wrong-file
git commit -m "feat(scope): the correct message"
```

### "I committed a secret" 🚨

**Stop. Do not just delete it and commit again** — it stays in the git history forever.

1. **Tell the team lead immediately.** Speed matters more than embarrassment.
2. **Rotate the secret** — assume it is compromised, because it is.
3. The lead will purge it from history.

This is exactly why we have `.env` in `.gitignore` and secret scanning enabled. It happens. Say
something quickly and it is a five-minute problem.

### "My PR has 40 commits and they're all called wip"

That is fine. We squash-merge, so it becomes one commit on `main`. Just make sure the **PR
title** is a proper conventional commit message, because that is what gets used.

### "CI is failing and I don't understand why"

1. Click the failing check in the PR.
2. Read the log from the **bottom up** — the real error is usually near the end.
3. Reproduce it locally with `pnpm verify`.
4. Still stuck? Paste the error in the team channel. Do not sit with it.

### "I deleted something important"

Almost nothing is truly lost in git:

```bash
git reflog
```

This lists everywhere `HEAD` has been. Find the state you want and `git reset --hard <hash>`.

---

## 9. Project-specific traps

Things that are easy to get wrong in *this* codebase specifically.

### Money is never a decimal

```csharp
decimal price = 1500.00m;      // ❌ the build will fail
long priceMinor = 150000;      // ✅ ₦1,500.00 in kobo
```

Floating-point arithmetic loses fractions of kobo. In a ledger that must balance to the last
unit, that is unrecoverable. A build analyser enforces this.

### Never bypass the tenant filter

Queries are automatically scoped to the current agency. If you write
`.IgnoreQueryFilters()`, you are reading **every agency's data at once**. There are two or three
legitimate uses of it in the entire codebase, all in platform admin reporting, all audited.

**If you think you need it, ask first.** A leak here means one travel agency sees another's
customers and prices.

### Prices are snapshotted, never recalculated

When an order is placed, the net rate, markup and tax are copied onto the order line and frozen
there. Do not "helpfully" recalculate from the current markup rules — an agent changing their
markup next month must not silently rewrite last month's revenue.

### The supplier has no webhooks

Trips Africa will not tell us when a ticket is issued. We poll. If you are adding anything that
depends on a booking's final state, it belongs in a background job, not a request handler. See
`services/TripsAgent.Worker/Jobs/`.

### Everything the traveller sees is the agent's brand

No hard-coded "Trips" in storefront pages, invoices, vouchers or customer emails. Pull the name,
logo and colours from the agency's branding record. The traveller must not know we exist.

### Never edit generated files

`packages/api-client/` and `packages/contracts/` are generated from the .NET code. Edit the C#
DTO and run `pnpm generate:api`. CI fails if the committed client is out of date.

---

## 10. Getting help

**Try for 30 minutes, then ask.** That is the rule.

Before asking, have these ready — half the time, assembling them solves the problem:

1. What you are trying to do
2. What you expected
3. What actually happened (the **exact** error text, not a paraphrase)
4. What you have already tried

Ask in the **team channel**, not by direct message. Someone else has the same question, and the
answer becomes searchable for the next person.

There are no stupid questions in this repo. This is a large system, the travel domain has its
own vocabulary, and nobody on this team has seen all of it before. Asking early is the
professional move; quietly burning two days is not.

---

## Quick reference

```bash
# start a task
git checkout main && git pull origin main
git checkout -b feat/M1-my-task

# work, then check
pnpm verify

# commit and push
git add -p
git commit -m "feat(scope): what this does"
git push -u origin feat/M1-my-task
gh pr create --fill

# main moved
git rebase main
git push --force-with-lease

# panic
git reflog
```

---

*Something here wrong, missing, or confusing? Open a PR. Improving this guide counts as real work.*
