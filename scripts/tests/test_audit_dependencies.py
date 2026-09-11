"""
Tests for scripts/audit_dependencies.py — the gate behind .github/workflows/dependency-audit.yml.

    python3 -m unittest discover -s scripts/tests

The fixtures are real output from the real tools, trimmed and with paths made neutral:
dotnet-top-level.json and dotnet-transitive.json come from scratch projects referencing
Newtonsoft.Json 12.0.1 / 11.0.1 and System.Text.Encodings.Web 4.5.0; dotnet-clean.json is this
solution; pnpm-audit.json is this workspace before vitest and postcss were fixed. If the tools
change their output shape, regenerate a fixture rather than hand-editing one to fit the parser.
"""
import datetime as dt
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import audit_dependencies as audit  # noqa: E402

FIXTURES = Path(__file__).resolve().parent / "fixtures"
SCRIPT = Path(__file__).resolve().parent.parent / "audit_dependencies.py"
TODAY = dt.date(2026, 9, 11)


def fixture(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text())


def run(stack: str, report: dict, allowlist: str = "") -> tuple[int, str]:
    out = io.StringIO()
    return audit.audit(stack, report, allowlist, TODAY, out=out), out.getvalue()


def dotnet_report(severity: str, advisory: str = "GHSA-aaaa-bbbb-cccc") -> dict:
    return {"projects": [{"path": "/repo/backend/A/A.csproj", "frameworks": [{
        "framework": "net10.0",
        "transitivePackages": [{"id": "Some.Package", "resolvedVersion": "1.0.0",
                                "vulnerabilities": [{
                                    "severity": severity,
                                    "advisoryurl": f"https://github.com/advisories/{advisory}"}]}],
    }]}]}


class ReadsRealDotnetOutput(unittest.TestCase):
    def test_top_level_packages_are_found_with_their_severity(self):
        found = {f.advisory: f for f in audit.parse_dotnet(fixture("dotnet-top-level.json"))}
        self.assertEqual(found["GHSA-5CRP-9R3C-P9VR"].severity, "high")
        self.assertEqual(found["GHSA-5CRP-9R3C-P9VR"].package, "Newtonsoft.Json")
        self.assertEqual(found["GHSA-GHHP-997W-QR28"].severity, "critical")

    def test_transitive_packages_are_found_too(self):
        [finding] = audit.parse_dotnet(fixture("dotnet-transitive.json"))
        self.assertEqual((finding.package, finding.version), ("Newtonsoft.Json", "11.0.1"))
        self.assertEqual(finding.where, {"TripsAgent.Worker (transitive)"})

    def test_this_solution_today_is_clean(self):
        self.assertEqual(audit.parse_dotnet(fixture("dotnet-clean.json")), [])

    def test_the_same_advisory_in_two_projects_is_one_finding(self):
        report = dotnet_report("High")
        report["projects"].append(json.loads(json.dumps(report["projects"][0])))
        report["projects"][1]["path"] = "/repo/backend/B/B.csproj"
        [finding] = audit.parse_dotnet(report)
        self.assertEqual(finding.where, {"A (transitive)", "B (transitive)"})


class ReadsRealPnpmOutput(unittest.TestCase):
    def test_every_advisory_is_found_keyed_by_ghsa(self):
        found = {f.advisory: f for f in audit.parse_pnpm(fixture("pnpm-audit.json"))}
        self.assertEqual(found["GHSA-5XRQ-8626-4RWP"].severity, "critical")
        self.assertEqual(found["GHSA-5XRQ-8626-4RWP"].package, "vitest")
        self.assertEqual(found["GHSA-5XRQ-8626-4RWP"].fixed_in, ">=3.2.6")
        self.assertEqual(found["GHSA-R28C-9Q8G-F849"].package, "postcss")
        self.assertIn("apps/storefront > next@15.5.25 > postcss@8.4.31",
                      found["GHSA-R28C-9Q8G-F849"].where)

    def test_counts_match_what_pnpm_itself_summarised(self):
        report = fixture("pnpm-audit.json")
        severities = [f.severity for f in audit.parse_pnpm(report)]
        expected = report["metadata"]["vulnerabilities"]
        for level in ("moderate", "high", "critical"):
            self.assertEqual(severities.count(level), expected[level], level)


class TheThreshold(unittest.TestCase):
    def test_high_fails(self):
        self.assertEqual(run("dotnet", dotnet_report("High"))[0], 1)

    def test_critical_fails(self):
        self.assertEqual(run("dotnet", dotnet_report("Critical"))[0], 1)

    def test_moderate_and_low_are_reported_but_pass(self):
        for severity in ("Moderate", "Low"):
            code, output = run("dotnet", dotnet_report(severity))
            self.assertEqual(code, 0, severity)
            self.assertIn("Some.Package", output)

    def test_a_severity_it_cannot_read_fails_rather_than_passing(self):
        self.assertEqual(run("dotnet", dotnet_report("Severe"))[0], 1)

    def test_the_real_pnpm_report_fails_and_names_what_blocks(self):
        code, output = run("pnpm", fixture("pnpm-audit.json"))
        self.assertEqual(code, 1)
        blocking = output.split("BLOCKING", 1)[1]
        for advisory in ("GHSA-5XRQ-8626-4RWP", "GHSA-FX2H-PF6J-XCFF",
                         "GHSA-6G55-P6WH-862Q", "GHSA-R28C-9Q8G-F849"):
            self.assertIn(advisory, blocking)
        self.assertNotIn("GHSA-67MH-4WV8-2F99", blocking)  # esbuild, moderate
        self.assertIn(audit.RUNBOOK, output)

    def test_a_clean_report_passes(self):
        self.assertEqual(run("dotnet", fixture("dotnet-clean.json"))[0], 0)


class TheAllowlist(unittest.TestCase):
    ENTRY = "GHSA-aaaa-bbbb-cccc  2026-10-01  no fix upstream yet, tracked in #131"

    def test_an_allowlisted_high_passes_and_is_still_shown(self):
        code, output = run("dotnet", dotnet_report("High"), self.ENTRY)
        self.assertEqual(code, 0)
        self.assertIn("Allowlisted", output)

    def test_it_only_covers_the_advisory_it_names(self):
        self.assertEqual(run("dotnet", dotnet_report("High", "GHSA-dddd-eeee-ffff"),
                             self.ENTRY)[0], 1)

    def test_a_reason_may_cite_an_issue_number_after_a_hash(self):
        entries, problems = audit.load_allowlist(self.ENTRY, TODAY)
        self.assertEqual(problems, [])
        self.assertEqual(entries["GHSA-AAAA-BBBB-CCCC"].reason, "no fix upstream yet, tracked in #131")

    def test_comments_and_blank_lines_are_ignored(self):
        entries, problems = audit.load_allowlist("# a comment\n\n   # indented\n", TODAY)
        self.assertEqual((entries, problems), ({}, []))

    def test_an_expired_entry_fails_even_though_the_advisory_is_listed(self):
        code, output = run("dotnet", dotnet_report("High"),
                           "GHSA-aaaa-bbbb-cccc  2026-09-10  was fine last month")
        self.assertEqual(code, 1)
        self.assertIn("due for review", output)

    def test_an_expired_entry_fails_even_when_nothing_is_vulnerable(self):
        code, _ = run("dotnet", fixture("dotnet-clean.json"),
                      "GHSA-aaaa-bbbb-cccc  2026-09-10  was fine last month")
        self.assertEqual(code, 1)

    def test_the_review_date_is_today_at_the_earliest(self):
        _, problems = audit.load_allowlist("GHSA-aaaa-bbbb-cccc  2026-09-11  reason", TODAY)
        self.assertEqual(problems, [])

    def test_a_review_date_too_far_ahead_is_refused(self):
        _, problems = audit.load_allowlist("GHSA-aaaa-bbbb-cccc  2026-12-11  reason", TODAY)
        self.assertEqual(len(problems), 1)
        self.assertIn("90 days", problems[0])
        _, problems = audit.load_allowlist("GHSA-aaaa-bbbb-cccc  2026-12-10  reason", TODAY)
        self.assertEqual(problems, [])

    def test_malformed_lines_are_problems(self):
        for line in ("GHSA-aaaa-bbbb-cccc  2026-10-01",           # no reason
                     "CVE-2024-1234  2026-10-01  reason",         # not a GHSA id
                     "GHSA-aaaa-bbbb-cccc  01/10/2026  reason",   # not ISO
                     self.ENTRY + "\n" + self.ENTRY):             # duplicated
            _, problems = audit.load_allowlist(line, TODAY)
            self.assertEqual(len(problems), 1, line)

    def test_a_bad_allowlist_fails_the_audit(self):
        self.assertEqual(run("dotnet", fixture("dotnet-clean.json"), "nonsense")[0], 1)

    def test_an_entry_nothing_needs_any_more_is_pointed_out_but_passes(self):
        code, output = run("dotnet", fixture("dotnet-clean.json"), self.ENTRY)
        self.assertEqual(code, 0)
        self.assertIn("no longer found", output)

    def test_the_committed_allowlists_are_valid_today(self):
        for stack, config in audit.STACKS.items():
            text = (audit.REPO_ROOT / config["allowlist"]).read_text()
            _, problems = audit.load_allowlist(text, dt.date.today())
            self.assertEqual(problems, [], stack)


class AnAuditThatDidNotRunNeverPasses(unittest.TestCase):
    def test_dotnet_output_without_projects(self):
        for report in ({}, {"projects": []}, [], {"projects": [], "problems": ["x"]}):
            with self.assertRaises(audit.AuditDidNotRun):
                audit.parse_dotnet(report)

    def test_dotnet_project_problems(self):
        report = dotnet_report("High")
        report["projects"][0]["problems"] = [{"text": "No assets file was found"}]
        with self.assertRaises(audit.AuditDidNotRun):
            audit.parse_dotnet(report)

    def test_pnpm_error_or_missing_advisories(self):
        for report in ({"error": {"code": "ERR_PNPM_AUDIT_BAD_RESPONSE"}}, {"actions": []}, []):
            with self.assertRaises(audit.AuditDidNotRun):
                audit.parse_pnpm(report)

    def test_an_unrestored_project_is_refused(self):
        # dotnet prints the same JSON for a project it never restored as for a clean one.
        with tempfile.TemporaryDirectory() as root:
            restored, unrestored = Path(root, "A"), Path(root, "B")
            (restored / "obj").mkdir(parents=True)
            (restored / "obj" / "project.assets.json").write_text("{}")
            unrestored.mkdir()
            report = {"projects": [{"path": str(restored / "A.csproj")},
                                   {"path": str(unrestored / "B.csproj")}]}
            with self.assertRaisesRegex(audit.AuditDidNotRun, "B.csproj"):
                audit.check_restored(report)
            report["projects"].pop()
            audit.check_restored(report)


class TheCommandLine(unittest.TestCase):
    """Exit codes are what CI reads, so prove them through the real entry point."""

    def invoke(self, *args: str) -> int:
        return subprocess.run([sys.executable, str(SCRIPT), *args, "--today", "2026-09-11",
                               "--allowlist", "/nonexistent"], capture_output=True).returncode

    def test_exit_codes(self):
        self.assertEqual(self.invoke("pnpm", "--report", str(FIXTURES / "pnpm-audit.json")), 1)
        self.assertEqual(self.invoke("dotnet", "--report", str(FIXTURES / "dotnet-clean.json")), 0)
        self.assertEqual(
            self.invoke("dotnet", "--report", str(FIXTURES / "dotnet-transitive.json")), 1)
        with tempfile.NamedTemporaryFile("w", suffix=".json") as garbage:
            garbage.write("Welcome to .NET! not json")
            garbage.flush()
            self.assertEqual(self.invoke("dotnet", "--report", garbage.name), 2)
        self.assertEqual(self.invoke("pnpm", "--report", str(FIXTURES / "dotnet-clean.json")), 2)


if __name__ == "__main__":
    unittest.main()
