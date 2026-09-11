#!/usr/bin/env python3
"""
Fails when a dependency has a known High or Critical vulnerability.

    python3 scripts/audit_dependencies.py dotnet    # backend: every NuGet package, transitive too
    python3 scripts/audit_dependencies.py pnpm      # frontend: every npm package in the lockfile

Runs in CI (.github/workflows/dependency-audit.yml) and works the same from any directory.

Why a script instead of calling the tools directly
--------------------------------------------------
Neither tool can be the gate on its own:

* `dotnet list package --vulnerable` exits 0 whatever it finds. It has no severity threshold,
  and a project that was never restored reports exactly the same JSON as a clean one — so a
  broken CI step would look like a pass.
* `pnpm audit --audit-level high` has a threshold, but no way to record *why* an advisory is
  being tolerated or *until when*.

Both print JSON, so this reads that JSON and applies one rule to both stacks:

  High or Critical  -> fail, unless the advisory is in the stack's VulnerabilityAllowlist.txt
  Moderate or Low   -> printed, never fails
  Anything unknown  -> fail. A severity we cannot read is not one we can call safe.

Exit codes: 0 pass · 1 a blocking advisory or a bad allowlist · 2 the audit could not run.

When this fails, docs/runbooks/vulnerable-dependency.md says what to do.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
RUNBOOK = "docs/runbooks/vulnerable-dependency.md"

# The threshold issue #108 set. Everything else is reported and allowed.
BLOCKING = {"high", "critical"}
NON_BLOCKING = {"info", "low", "moderate"}

# An allowlist entry is a decision to live with a known hole for a while. Capping how far ahead
# its review date can be stops "2099-01-01" from quietly turning a decision into a permanent one.
MAX_REVIEW_DAYS = 90

GHSA = re.compile(r"GHSA(?:-[0-9a-z]{4}){3}", re.IGNORECASE)

STACKS = {
    "dotnet": {"root": "backend", "allowlist": "backend/VulnerabilityAllowlist.txt"},
    "pnpm": {"root": "frontend", "allowlist": "frontend/VulnerabilityAllowlist.txt"},
}


class AuditDidNotRun(Exception):
    """The tool failed or printed something we cannot trust. Never treated as a pass."""


@dataclass
class Finding:
    advisory: str  # GHSA id, upper case — the key the allowlist uses
    package: str
    version: str
    severity: str  # lower case
    url: str
    where: set[str] = field(default_factory=set)  # projects or dependency paths
    fixed_in: str = ""

    @property
    def blocks(self) -> bool:
        return self.severity not in NON_BLOCKING


@dataclass
class AllowlistEntry:
    advisory: str
    review_by: dt.date
    reason: str
    line: int


# ------------------------------------------------------------------ reading the tools' output


def _advisory_id(*candidates: str | None) -> str:
    for text in candidates:
        if text and (match := GHSA.search(text)):
            return match.group(0).upper()
    # No GHSA id: fall back to whatever identifies it, so it still reports and still blocks.
    return next((c for c in candidates if c), "unknown-advisory")


def _merge(findings: dict[tuple[str, str, str], Finding], finding: Finding) -> None:
    key = (finding.advisory, finding.package, finding.version)
    if key in findings:
        findings[key].where |= finding.where
    else:
        findings[key] = finding


def parse_dotnet(report: object) -> list[Finding]:
    """Reads `dotnet list package --vulnerable --include-transitive --format json`."""
    if not isinstance(report, dict) or not isinstance(report.get("projects"), list):
        raise AuditDidNotRun("the dotnet report has no 'projects' list")
    if not report["projects"]:
        raise AuditDidNotRun("the dotnet report lists no projects — was the solution found?")
    if report.get("problems"):
        raise AuditDidNotRun(f"dotnet reported problems: {report['problems']}")

    findings: dict[tuple[str, str, str], Finding] = {}
    for project in report["projects"]:
        if project.get("problems"):
            raise AuditDidNotRun(f"{project.get('path')}: {project['problems']}")
        name = Path(str(project.get("path", "?"))).stem
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for vuln in package.get("vulnerabilities", []):
                        url = str(vuln.get("advisoryurl", ""))
                        label = name if kind == "topLevelPackages" else f"{name} (transitive)"
                        _merge(findings, Finding(
                            advisory=_advisory_id(url),
                            package=str(package.get("id", "?")),
                            version=str(package.get("resolvedVersion", "?")),
                            severity=str(vuln.get("severity", "unknown")).lower(),
                            url=url,
                            where={label},
                        ))
    return list(findings.values())


def parse_pnpm(report: object) -> list[Finding]:
    """Reads `pnpm audit --json`."""
    if not isinstance(report, dict):
        raise AuditDidNotRun("the pnpm report is not a JSON object")
    if "error" in report:
        raise AuditDidNotRun(f"pnpm audit failed: {report['error']}")
    if not isinstance(report.get("advisories"), dict):
        raise AuditDidNotRun("the pnpm report has no 'advisories' object")

    findings: dict[tuple[str, str, str], Finding] = {}
    for advisory in report["advisories"].values():
        for found in advisory.get("findings", []) or [{}]:
            _merge(findings, Finding(
                advisory=_advisory_id(advisory.get("github_advisory_id"), advisory.get("url"),
                                      str(advisory.get("id", ""))),
                package=str(advisory.get("module_name", "?")),
                version=str(found.get("version", "?")),
                severity=str(advisory.get("severity", "unknown")).lower(),
                url=str(advisory.get("url", "")),
                where=set(found.get("paths", [])),
                fixed_in=str(advisory.get("patched_versions", "")),
            ))
    return list(findings.values())


def check_restored(report: dict) -> None:
    """dotnet reports an unrestored project exactly like a clean one. Refuse to believe that."""
    missing = [
        p["path"] for p in report.get("projects", [])
        if not (Path(p["path"]).parent / "obj" / "project.assets.json").is_file()
    ]
    if missing:
        raise AuditDidNotRun(
            "these projects were never restored, so nothing was audited: " + ", ".join(missing)
            + " — run `dotnet restore TripsAgent.slnx` first")


# ------------------------------------------------------------------ the allowlist


def load_allowlist(text: str, today: dt.date) -> tuple[dict[str, AllowlistEntry], list[str]]:
    """
    One entry per line:   GHSA-xxxx-xxxx-xxxx  YYYY-MM-DD  why, and the tracking issue
    Returns the entries and a list of problems. Any problem fails the audit.
    """
    entries: dict[str, AllowlistEntry] = {}
    problems: list[str] = []
    latest = today + dt.timedelta(days=MAX_REVIEW_DAYS)

    for number, raw in enumerate(text.splitlines(), start=1):
        # Only whole-line comments: a reason is expected to cite an issue, like "#131".
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(None, 2)
        where = f"line {number}"
        if len(parts) < 3:
            problems.append(f"{where}: needs an advisory id, a review-by date and a reason")
            continue
        advisory, date_text, reason = parts
        if not GHSA.fullmatch(advisory):
            problems.append(f"{where}: '{advisory}' is not a GHSA id (GHSA-xxxx-xxxx-xxxx)")
            continue
        try:
            review_by = dt.date.fromisoformat(date_text)
        except ValueError:
            problems.append(f"{where}: '{date_text}' is not a date (YYYY-MM-DD)")
            continue
        advisory = advisory.upper()
        if advisory in entries:
            problems.append(f"{where}: {advisory} is listed twice")
        elif review_by < today:
            problems.append(
                f"{where}: {advisory} was due for review on {review_by} — look at it again: "
                "upgrade if a fix exists now, or set a new date with a fresh reason")
        elif review_by > latest:
            problems.append(
                f"{where}: {advisory} review date {review_by} is more than {MAX_REVIEW_DAYS} "
                f"days away (latest allowed: {latest})")
        else:
            entries[advisory] = AllowlistEntry(advisory, review_by, reason.strip(), number)
    return entries, problems


# ------------------------------------------------------------------ deciding


def run_tool(stack: str) -> dict:
    root = REPO_ROOT / STACKS[stack]["root"]
    if stack == "dotnet":
        command = ["dotnet", "list", "TripsAgent.slnx", "package",
                   "--vulnerable", "--include-transitive", "--format", "json"]
    else:
        command = ["pnpm", "audit", "--json"]
    try:
        result = subprocess.run(command, cwd=root, capture_output=True, text=True, check=False)
    except FileNotFoundError as error:
        raise AuditDidNotRun(f"{command[0]} is not installed: {error}") from error
    # pnpm exits 1 whenever it finds anything, even Low. Its exit code tells us nothing; whether
    # the output parses does. dotnet exits 0 either way.
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise AuditDidNotRun(
            f"`{' '.join(command)}` exited {result.returncode} without JSON.\n"
            f"stdout: {result.stdout[-2000:]}\nstderr: {result.stderr[-2000:]}") from error


def audit(stack: str, report: dict, allowlist_text: str, today: dt.date,
          out=sys.stdout) -> int:
    findings = parse_dotnet(report) if stack == "dotnet" else parse_pnpm(report)
    allowed, problems = load_allowlist(allowlist_text, today)
    allowlist_path = STACKS[stack]["allowlist"]

    blocking = [f for f in findings if f.blocks and f.advisory not in allowed]
    tolerated = [f for f in findings if f.blocks and f.advisory in allowed]
    minor = [f for f in findings if not f.blocks]
    stale = sorted(set(allowed) - {f.advisory for f in findings})

    def show(title: str, items: list[Finding]) -> None:
        if not items:
            return
        print(f"\n{title}", file=out)
        for f in sorted(items, key=lambda f: (f.severity, f.package)):
            print(f"  [{f.severity}] {f.package} {f.version}  {f.advisory}  {f.url}", file=out)
            if f.fixed_in:
                print(f"      fixed in: {f.fixed_in}", file=out)
            for place in sorted(f.where)[:3]:
                print(f"      via: {place}", file=out)
            if len(f.where) > 3:
                print(f"      … and {len(f.where) - 3} more", file=out)

    print(f"{stack}: {len(findings)} advisories — {len(blocking)} blocking, "
          f"{len(tolerated)} allowlisted, {len(minor)} below the threshold", file=out)
    show("Below the threshold (reported, not blocking):", minor)
    show(f"Allowlisted in {allowlist_path}:", tolerated)
    for advisory in stale:
        print(f"\nNote: {advisory} is allowlisted but no longer found — remove it from "
              f"{allowlist_path}.", file=out)
    show("BLOCKING — High or Critical, not allowlisted:", blocking)

    if problems:
        print(f"\n{allowlist_path} has problems:", file=out)
        for problem in problems:
            print(f"  {problem}", file=out)

    if blocking or problems:
        print(f"\nFAILED. What to do: {RUNBOOK}", file=out)
        return 1
    print("\nPassed.", file=out)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("stack", choices=sorted(STACKS))
    parser.add_argument("--report", type=Path,
                        help="read a saved JSON report instead of running the tool")
    parser.add_argument("--allowlist", type=Path, help="default: the stack's own allowlist")
    parser.add_argument("--today", type=dt.date.fromisoformat, default=dt.date.today(),
                        help=argparse.SUPPRESS)  # for tests
    args = parser.parse_args(argv)

    allowlist = args.allowlist or REPO_ROOT / STACKS[args.stack]["allowlist"]
    try:
        if args.report:
            report = json.loads(args.report.read_text())
        else:
            report = run_tool(args.stack)
            if args.stack == "dotnet":
                check_restored(report)
        text = allowlist.read_text() if allowlist.is_file() else ""
        return audit(args.stack, report, text, args.today)
    except (AuditDidNotRun, json.JSONDecodeError, OSError) as error:
        print(f"The {args.stack} audit did not run, so it cannot pass: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
