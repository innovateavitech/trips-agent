---
name: verify
description: Run the same checks CI runs, from the right directories, before calling a change done. Use before saying any backend or frontend change is finished, before committing, and before opening a pull request.
user-invocable: true
---

# Verify

"Done" means these commands ran and passed — not that the code looks right. This repository has
two self-contained roots, each checked from its own directory. A command run from the wrong one
fails in a way that looks like a broken build, or succeeds against nothing at all.

Run the steps in order and **report every command you ran and its result**. If a step could not
run — Docker not started, pnpm not installed — say so plainly. An unrun check is not a passing one.

## 0. Sync-conflict copies — from the repository root

iCloud Desktop sync and Dropbox leave `Foo 2.cs` beside `Foo.cs` after checkouts and builds. .NET
compiles every `*.cs` it finds, so one copy breaks the build with a wall of CS0101 errors — and
they are git-ignored, so `git status` looks clean.

```bash
find . \( -name "* [0-9].*" -o -type d -name "* [0-9]" \) -not -path "*/node_modules/*" -not -path "*/.git/*" \
       -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*/.next/*" -not -path "*/.turbo/*"
```

Expect no output. Directories get copied too (`components 2/`), often empty. For each file found, compare
it with its original: `cmp "Foo 2.cs" Foo.cs`; for a directory, `diff -rq "components 2" components`.
Delete it only if they are identical. If it **differs**, stop and show the user the difference —
a conflict copy can hold an edit that exists nowhere else.

## 1. Backend — from `backend/`

When anything under `backend/` changed:

```bash
cd backend
dotnet build TripsAgent.slnx                        # warnings are errors; TRIPS001/TRIPS002 run here
dotnet test TripsAgent.slnx                         # integration tests start PostgreSQL in Docker
dotnet format TripsAgent.slnx --verify-no-changes
../scripts/ef.sh check                              # an entity changed without a migration
```

`dotnet test` needs Docker running. Without it the integration tests cannot start their database —
report that rather than skipping past it.

## 2. Frontend — from `frontend/`

When anything under `frontend/` changed:

```bash
cd frontend
pnpm verify        # prettier, eslint, typecheck, check:design and tests, in that order
```

## 3. What is about to be committed — from the repository root

```bash
git status --short
```

Look for files you did not mean to create: a throwaway test, scratch output, a `.env`.

## Reporting

Finish with each command and whether it passed. Call the change done only when every step that
applies passed. If only one side changed, say which steps you skipped and why.
