# Branch protection setup

One-time configuration for `innovateavitech/trips-agent`. Run this **before** the first feature
PR, so the rules are in place from day one rather than retrofitted after someone has already
pushed to `main`.

**Goal:** nobody can push directly to `main`. Every change arrives via a reviewed pull request.
The repository owner keeps an emergency override.

Requires the [`gh` CLI](https://cli.github.com/), authenticated as a repository admin
(`gh auth login`).

---

## Step 1 — Repository merge settings

Squash-merge only. This is the single highest-leverage setting for a team that is new to git:
messy work-in-progress history stays on the branch and vanishes on merge, `main` gets exactly
one clean commit per change, and reverting is trivial.

```bash
gh api -X PATCH repos/innovateavitech/trips-agent \
  -F allow_squash_merge=true \
  -F allow_merge_commit=false \
  -F allow_rebase_merge=false \
  -F delete_branch_on_merge=true \
  -F allow_auto_merge=true \
  -f squash_merge_commit_title=PR_TITLE \
  -f squash_merge_commit_message=PR_BODY
```

`squash_merge_commit_title=PR_TITLE` is why `CONTRIBUTING.md` insists the **PR title** be a
conventional commit — it becomes the commit message on `main`.

---

## Step 2 — Protect `main`

> ⚠️ **Read this before running it.** The `contexts` array lists CI jobs that must pass before a
> merge is allowed. If you list a job that does not exist yet, **every PR will be blocked
> forever**, because a check that never runs never passes.
>
> So: run this now with `"contexts": []`, and come back to Step 4 to add the checks once CI is
> actually running.

```bash
gh api -X PUT repos/innovateavitech/trips-agent/branches/main/protection \
  --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": []
  },
  "enforce_admins": false,
  "required_pull_request_reviews": {
    "required_approving_review_count": 1,
    "dismiss_stale_reviews": true,
    "require_code_owner_reviews": true,
    "require_last_push_approval": false
  },
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true,
  "block_creations": false,
  "lock_branch": false
}
JSON
```

### What each setting does

| Setting | Effect |
|---|---|
| `required_pull_request_reviews` | No direct pushes. Everything needs a PR |
| `required_approving_review_count: 1` | At least one other person must approve |
| `dismiss_stale_reviews: true` | Pushing new commits clears prior approvals — nobody sneaks a change in after review |
| `require_code_owner_reviews: true` | Payments, migrations, tenancy and supplier changes must be approved by their owner in `CODEOWNERS` |
| `required_conversation_resolution` | Every review comment must be resolved before merge |
| `strict: true` | The branch must be up to date with `main` before merging |
| `required_linear_history` | No merge commits. Pairs with squash-merge |
| `allow_force_pushes: false` | Nobody can rewrite `main`'s history |
| `allow_deletions: false` | Nobody can delete `main` |
| **`enforce_admins: false`** | **Repository admins can bypass all of the above.** This is your emergency override |

### About `enforce_admins: false`

This is the "except maybe me" you asked for. It lets **any repository admin** push directly to
`main` when something genuinely cannot wait.

Two consequences worth being deliberate about:

1. **Only you should hold the Admin role.** Everyone else gets **Write**. Check who has what:

   ```bash
   gh api repos/innovateavitech/trips-agent/collaborators \
     --jq '.[] | "\(.login)\t\(.role_name)"'
   ```

   Demote anyone who does not need Admin:

   ```bash
   gh api -X PUT repos/innovateavitech/trips-agent/collaborators/USERNAME -f permission=push
   ```

2. **Use it almost never.** A bypass is invisible to the team and skips CI. If you want zero
   exceptions — including for yourself — set `enforce_admins` to `true` instead. You can flip it
   either way at any time without touching any code.

---

## Step 3 — Enable secret scanning

Catches an API key or password before it lands in history.

```bash
gh api -X PATCH repos/innovateavitech/trips-agent \
  --input - <<'JSON'
{
  "security_and_analysis": {
    "secret_scanning":                  { "status": "enabled" },
    "secret_scanning_push_protection":  { "status": "enabled" }
  }
}
JSON
```

Push protection is the important half: it **blocks the push** rather than emailing you after the
secret is already public.

> On a private repository this needs GitHub Advanced Security. If the call fails with a 403,
> the repo either needs to be public or needs a GHAS licence — until then, the `.gitignore`
> entry for `.env` and the pre-commit hook are the defence.

---

## Step 4 — Add required CI checks (once CI exists)

After `.github/workflows/ci.yml` has run successfully on at least one PR, make its jobs
mandatory:

```bash
gh api -X PATCH repos/innovateavitech/trips-agent/branches/main/protection/required_status_checks \
  --input - <<'JSON'
{
  "strict": true,
  "contexts": [
    "build-api",
    "test-api",
    "build-web",
    "test-web",
    "lint",
    "migrations",
    "api-client-freshness",
    "commitlint"
  ]
}
JSON
```

| Check | What it protects against |
|---|---|
| `build-api` | .NET build, warnings as errors |
| `test-api` | Unit + integration tests against a real Postgres |
| `build-web` | All four front ends compile |
| `test-web` | Component and unit tests |
| `lint` | ESLint, Prettier, `dotnet format` |
| `migrations` | Migrations apply cleanly to an empty database, and no model change is left un-migrated |
| `api-client-freshness` | The committed TypeScript client matches the current .NET DTOs |
| `commitlint` | The PR title is a valid conventional commit |

Add each context only once its job name matches exactly — the string must equal the job's name
in the workflow file.

---

## Step 5 — Verify it works

The only way to know protection is on is to try to break it.

```bash
git checkout main
git pull
echo "test" >> README.md
git commit -am "test: this should be rejected"
git push origin main
```

Expected, for a non-admin:

```
remote: error: GH006: Protected branch update failed for refs/heads/main.
remote: error: Changes must be made through a pull request.
```

Then undo the local commit:

```bash
git reset --hard origin/main
```

As an admin with `enforce_admins: false`, this push will **succeed** — which is the point. Ask a
teammate to run the same test from their account to confirm the rule bites for everyone else.

---

## Step 6 — Labels

```bash
gh label create "good-first-issue" --color 7057ff --description "Small, well-scoped, safe for a first PR" --force
gh label create "M1"               --color 0e8a16 --description "Milestone 1 — spine + ticketing" --force
gh label create "M2"               --color 1d76db --description "Milestone 2 — storefront + catalog" --force
gh label create "M3"               --color 5319e7 --description "Milestone 3 — network + back-office" --force
gh label create "blocked"          --color b60205 --description "Waiting on something else" --force
gh label create "needs-decision"   --color fbca04 --description "Waiting on a client answer" --force
```

Keep a standing supply of `good-first-issue` items. A new joiner whose first PR merges cleanly
in their first week stays engaged; one who spends two weeks stuck on the checkout saga does not.

**A `good-first-issue` must not touch** payments, the ledger, tenancy filters, or the supplier
integration. Those are where a well-meaning mistake costs money or leaks data.

---

## Checking the current state

```bash
# what protection is active
gh api repos/innovateavitech/trips-agent/branches/main/protection | jq

# who can bypass it
gh api repos/innovateavitech/trips-agent/collaborators --jq '.[] | "\(.login)\t\(.role_name)"'
```

---

## If you need to change something later

All of these are reversible at any time and none of them require touching code:

```bash
# no exceptions, not even for admins
gh api -X POST repos/innovateavitech/trips-agent/branches/main/protection/enforce_admins

# restore the admin override
gh api -X DELETE repos/innovateavitech/trips-agent/branches/main/protection/enforce_admins

# require two approvals once the team grows
gh api -X PATCH repos/innovateavitech/trips-agent/branches/main/protection/required_pull_request_reviews \
  -F required_approving_review_count=2
```

> Prefer clicking? Everything above is also at
> **Settings → Branches → Branch protection rules** and **Settings → General → Pull Requests**.
