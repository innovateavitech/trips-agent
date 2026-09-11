# Runbook: a dependency has a known vulnerability

## Symptom

The `audit-api` or `audit-web` check is red on a pull request, or the nightly `dependency-audit`
run on `main` failed. The log ends with:

```
BLOCKING — High or Critical, not allowlisted:
  [high] Some.Package 1.2.3  GHSA-xxxx-xxxx-xxxx  https://github.com/advisories/GHSA-xxxx-xxxx-xxxx
      fixed in: >=1.2.4
      via: apps/storefront > next@15.5.25 > some-package@1.2.3

FAILED. What to do: docs/runbooks/vulnerable-dependency.md
```

**Your pull request probably did not cause this.** Advisories are published every day. A package
that was fine yesterday can be known-vulnerable this morning, and the next pull request to run
the check is the one that finds out. Do not "fix" it by reverting your own change.

## How to confirm it

Run the same check on your machine, from the repository root:

```bash
cd backend && dotnet restore TripsAgent.slnx && cd ..
python3 scripts/audit_dependencies.py dotnet      # NuGet, transitive packages included

cd frontend && pnpm install && cd ..
python3 scripts/audit_dependencies.py pnpm        # every package in pnpm-lock.yaml
```

Exit code `0` passed, `1` found a blocking advisory (or a broken allowlist line), `2` means the
audit **could not run** — a network error, a missing restore, a tool that printed garbage. A `2`
is not a vulnerability; re-run the job, and if it persists read the error it printed.

Open the advisory link. Note three things before you do anything else:

1. **Which package, and is it direct or transitive?** The `via:` lines show the path. For NuGet
   the location says `(transitive)`; `pnpm why <package> -r` in `frontend/` shows who pulls it in.
2. **Is there a fixed version?** The `fixed in:` line (pnpm) or the advisory's "Patched versions".
3. **Where does the package run?** In production (the API, the Worker, the storefront server),
   in the browser, or only at build or test time (vitest, eslint, a bundler).

## Impact

| Where it runs                           | Severity | Urgency                                                                                         |
| --------------------------------------- | -------- | ----------------------------------------------------------------------------------------------- |
| API, Worker or storefront server        | Critical | **Today.** Tell the team lead. This code holds wallets, passports and payment callbacks        |
| API, Worker or storefront server        | High     | This week. Nothing merges to `main` until it is fixed or allowlisted, so it cannot wait long    |
| Browser bundle (agent-console, etc.)    | High+    | This week                                                                                       |
| Build or test tooling only              | High+    | Still fix it — build tools run in CI with the repository's token — but it is not an emergency   |

## Fix

Work through these in order. Stop at the first one that works. Each is a normal branch and pull
request (`fix/deps-GHSA-xxxx`), reviewed like any other change.

### 1. A fixed version exists — upgrade

**Direct dependency.** Bump it.

- NuGet: change the `Version` in `backend/Directory.Packages.props` (every version lives there).
- pnpm: `cd frontend && pnpm update <package> -r --latest`, or edit the version in the
  `package.json` that lists it, then `pnpm install`.

**Transitive dependency.** First try upgrading the direct dependency that pulls it in — its newer
release may already depend on the fixed version. If not, force the transitive one forward:

- NuGet: add a `<PackageVersion>` for the vulnerable package under the
  `Transitive pins (security)` group in `backend/Directory.Packages.props`.
  `CentralPackageTransitivePinningEnabled` is on, so that version wins everywhere. Copy the
  comment style already there: which package pulls it in, the advisory id, and when to remove it.
- pnpm: add an override in `frontend/package.json`, scoped to the parent that pulls it in so it
  does not move the package for anyone else:

  ```json
  "pnpm": { "overrides": { "next>postcss": "^8.5.28" } }
  ```

  then `pnpm install`. Prefer staying inside the same major version. A major bump through an
  override can break the parent package in ways its own tests would have caught and ours won't.

Then run the full [verify](../../.claude/skills/verify/SKILL.md) steps — an upgrade is a code
change — and re-run the audit to see it pass.

### 2. No fixed version exists yet — the case that will definitely happen

A High advisory on a transitive dependency, and upstream has not shipped a fix. You cannot
upgrade your way out, and a red check blocks every pull request for everyone. This is what the
allowlist is for, and **it is a security decision, not a way to turn the build green**.

1. **Look for a way out that does not need a fix.**
   - Is there a patched fork or a newer major that drops the dependency entirely?
   - Can we stop depending on the parent package — is it doing something small we could do
     ourselves, or is it only in a dev tool we could swap?
   - Dev and test tooling: can the vulnerable path simply be removed from the lockfile?
2. **Decide whether we are actually exposed.** Read the advisory's description, not just its
   score. Most advisories need a specific call or a specific input. Write down, in one or two
   sentences:
   - which of our code reaches the vulnerable function, or why none does;
   - whether an attacker can control the input that triggers it (a traveller on a storefront can;
     a build script cannot);
   - what else limits the damage — tenant isolation, authentication in front of the endpoint,
     the code only running at build time.

   If you cannot say why we are not exposed, **stop and escalate** (below). "Probably fine" is
   not a reason.
3. **Mitigate what you can in our own code** — validate the input before it reaches the package,
   put the feature behind authentication, disable the vulnerable option. Link that change.
4. **Open a tracking issue** titled `Security: GHSA-xxxx-xxxx-xxxx in <package>`, labelled
   `module:security`, with the advisory link, the exposure reasoning from step 2 and the
   mitigation from step 3. Subscribe to the advisory on GitHub so you hear when a fix ships.
5. **Add one line to the allowlist** for that stack — `backend/VulnerabilityAllowlist.txt` or
   `frontend/VulnerabilityAllowlist.txt`:

   ```
   GHSA-xxxx-xxxx-xxxx  2026-12-01  transitive via next; only reachable from next dev, not the storefront server. #140
   ```

   - The GHSA id covers **that one advisory**. A second advisory on the same package still fails.
   - The date is when this decision expires. It **must** be within 90 days — the audit refuses
     anything later. On that date the check goes red again and someone looks afresh.
   - The reason is the sentence from step 2 plus the tracking issue number.
6. **Open the pull request.** CODEOWNERS routes both allowlist files to the team lead, whose
   approval is the actual decision. Say in the description which of steps 1–3 you tried.

**Critical** advisories and anything reachable by a traveller or an unauthenticated caller do
not go on the allowlist without the team lead agreeing _in the tracking issue_ first.

### 3. An allowlisted advisory's review date has arrived

The audit fails with `was due for review on …`. Go back to step 1: a fix may exist now. If it
still does not, repeat step 2 — re-check the exposure reasoning, set a new date no more than 90
days out, and update the reason. Do not just move the date.

When the audit prints `… is allowlisted but no longer found`, the fix has landed: delete the
line and close the tracking issue.

## If that doesn't work

- The audit exits `2` repeatedly: the registry or NuGet may be down. Check
  [githubstatus.com](https://www.githubstatus.com) and [status.nuget.org](https://status.nuget.org).
  The check is required, so it blocks merges until it can run — that is intentional. Only the
  team lead can merge past it, as an admin, and only for a change that cannot wait.
- You cannot tell whether we are exposed: ask the team lead, with the advisory link and the
  `via:` path. Do not allowlist while you wait.
- The advisory describes something being actively exploited in a package our production code
  uses: treat it as an incident. Tell the team lead immediately.

## Prevention

- **Dependabot** (`.github/dependabot.yml`) opens weekly pull requests for NuGet, pnpm and GitHub
  Actions, so we rarely fall far enough behind for an upgrade to be a big job. Merge them.
- **Dependabot security updates** (repository setting, `docs/BRANCH_PROTECTION.md` Step 3) opens a
  pull request the moment a fixed version exists for an advisory that affects us — which is
  usually how an allowlist entry ends.
- The nightly `dependency-audit` run on `main` means a new advisory is found in the morning, not
  in the middle of someone's unrelated pull request.
- Background: [issue #108](https://github.com/innovateavitech/trips-agent/issues/108).
