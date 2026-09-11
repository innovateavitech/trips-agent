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

## Step 3 — Security scanning: secrets, vulnerable dependencies, code

Five repository settings. The workflows in `.github/` do half the job; these switches do the rest,
and none of them can be turned on from a file in the repository.

### 3a. Secret scanning with push protection

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

Push protection is the important half: it **blocks the push** on GitHub's side rather than
emailing you after the secret is already public.

It is not a duplicate of `.githooks/pre-commit`. The hook only runs for someone who ran
`scripts/setup.sh`, and anyone can skip it with `--no-verify`. Push protection runs for every
push, from every machine, including a commit made in the GitHub web editor. Keep both: the hook
catches it before the commit exists, which is kinder; push protection is the one that cannot be
forgotten.

> On a private repository this needs GitHub Advanced Security (Secret Protection). If the call
> fails with a 403, the repository needs that licence — until then, the `.gitignore` entry for
> `.env` and the pre-commit hook are the only defence.

### 3b. Dependabot alerts and security updates

`.github/dependabot.yml` schedules weekly *version* updates. *Security* updates — a pull request
opened as soon as a fixed version exists for an advisory that affects us — are these settings:

```bash
gh api -X PUT    repos/innovateavitech/trips-agent/vulnerability-alerts
gh api -X PUT    repos/innovateavitech/trips-agent/automated-security-fixes
```

### 3c. CodeQL: advanced setup only

`.github/workflows/codeql.yml` is CodeQL's "advanced setup". GitHub rejects its results while
"default setup" is on, so make sure default setup is off:

```bash
gh api -X PATCH repos/innovateavitech/trips-agent/code-scanning/default-setup -f state=not-configured
```

### 3d. Block merges on serious code-scanning findings

The CodeQL job fails only when the *analysis* fails, not when it finds something. What blocks a
merge is a protection rule: **Settings → Rules → Rulesets → New branch ruleset**, target `main`,
enable **Require code scanning results**, tool `CodeQL`, alerts `Errors`, security alerts
`High or higher`.

This matches the dependency audit's threshold: High and Critical block, anything lower is shown
on the pull request and left to the reviewer.

### 3e. Check it took

```bash
gh api repos/innovateavitech/trips-agent --jq .security_and_analysis
```

Expect `secret_scanning`, `secret_scanning_push_protection` and `dependabot_security_updates` all
`"enabled"`. The field is only returned to an admin — `null` means your token is not one.

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
    "commitlint",
    "check-design",
    "audit-api",
    "audit-web",
    "codeql-csharp",
    "codeql-javascript-typescript"
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
| `commitlint` | The PR title is a valid conventional commit |
| `check-design` | A hard-coded colour, font or spacing value instead of a token (from `design.yml`) |
| `audit-api` | A NuGet package, direct or transitive, with a known High or Critical advisory (from `dependency-audit.yml`) |
| `audit-web` | The same for every package in `pnpm-lock.yaml` (from `dependency-audit.yml`) |
| `codeql-csharp` | CodeQL could not analyse the backend (from `codeql.yml`; findings block through Step 3d) |
| `codeql-javascript-typescript` | The same for the front ends and e2e tests |

Add each context only once its job name matches exactly — the string must equal the job's name
in the workflow file.

`audit-api` and `audit-web` can go red on a pull request that touched no dependency at all: a new
advisory was published overnight. That is deliberate, and
[the runbook](runbooks/vulnerable-dependency.md) says what to do — including when no fix exists
yet.

`api-client-freshness` is deliberately **not** in this list yet. `packages/api-client` is still
generated by a placeholder script, so there is no such job to require — and a required check
with no job behind it blocks every merge forever. Add it when the generator lands.

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
