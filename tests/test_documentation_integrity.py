"""Step definitions and Test Driver for documentation and data contract drift prevention."""

from __future__ import annotations

import datetime
import json
from pathlib import Path
import re
import subprocess
import sys
from typing import Any, Dict, List, Set

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.decision_trace import evaluate_workout_decision
from src.analysis.run_reviewer import evaluate_run_deep, extract_deep_run_metrics
from src.core.contracts import (
    validate_athlete_profile,
    validate_coach_history,
    validate_decision_trace_entry,
    validate_planning_markers,
    validate_run_review_evaluation,
)
from src.planner.extract_markers import extract_planning_markers

scenarios("documentation_integrity.feature")


class DocumentationIntegrityDriver:
    """Encapsulates documentation link verification, CLI parity checks, and contract validation."""

    ACTIVE_DOC_PATHS = [
        "GEMINI.md",
        "README.md",
        "docs/SYSTEM_SPEC.md",
        "docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md",
        "docs/AGENTIC_SIMULATION_PLAN.md",
        "docs/FORESIGHT_ENGINE_PLAN.md",
        "docs/WORKFLOW_DISPATCH_ARCHITECTURE_PLAN.md",
        "docs/archive/README.md",
        "tests/README.md",
        ".agents/skills/workout-scheduler/SKILL.md",
    ]

    def __init__(self, workspace_root: Path) -> None:
        self.workspace_root = workspace_root
        self.doc_files: List[Path] = []
        self.broken_links: List[str] = []
        self.legacy_mount_links: List[str] = []
        self.unarchived_plan_links: List[str] = []
        self.implemented_subcommands: Set[str] = set()
        self.help_output: str = ""
        self.readme_content: str = ""
        self.template_names: List[str] = []
        self.contract_errors: List[str] = []

    def load_active_docs(self) -> None:
        self.doc_files = [self.workspace_root / rel for rel in self.ACTIVE_DOC_PATHS]
        for path in self.doc_files:
            assert path.is_file(), f"Expected active documentation file is missing: {path}"

    def inspect_markdown_links(self) -> None:
        self.broken_links.clear()
        self.legacy_mount_links.clear()
        self.unarchived_plan_links.clear()

        link_pattern = re.compile(r"\]\(([^)]+)\)")
        for doc_path in self.doc_files:
            text = doc_path.read_text(encoding="utf-8")
            for match in link_pattern.finditer(text):
                raw_target = match.group(1).strip()
                if not raw_target or raw_target.startswith("#") or raw_target.startswith("http://") or raw_target.startswith("https://"):
                    continue

                if raw_target.startswith("file://") or raw_target.startswith("/home/") or raw_target.startswith("/mnt/"):
                    self.legacy_mount_links.append(f"{doc_path.relative_to(self.workspace_root)}: {raw_target}")

                if "docs/plans/" in raw_target and "docs/archive/plans/" not in raw_target:
                    self.unarchived_plan_links.append(f"{doc_path.name}: {raw_target}")

                # Strip line anchors (#L10-L20)
                clean_target = raw_target.split("#", 1)[0]
                if not clean_target:
                    continue

                if clean_target.startswith("file://"):
                    file_path = Path(clean_target.replace("file://", "", 1))
                else:
                    file_path = (doc_path.parent / clean_target).resolve()

                if not file_path.exists():
                    self.broken_links.append(f"{doc_path.relative_to(self.workspace_root)} -> {raw_target}")

    def assert_no_broken_links(self) -> None:
        assert not self.broken_links, f"Broken file links found in documentation:\n" + "\n".join(self.broken_links)

    def assert_no_legacy_mount_links(self) -> None:
        assert not self.legacy_mount_links, (
            f"Absolute paths or file:// schemes found in documentation links (use relative paths only):\n"
            + "\n".join(self.legacy_mount_links)
        )

    def assert_no_unarchived_plan_links(self) -> None:
        assert not self.unarchived_plan_links, (
            f"Unarchived docs/plans/ references found:\n" + "\n".join(self.unarchived_plan_links)
        )

    def load_cli_runner_subcommands(self) -> None:
        run_path = self.workspace_root / "run"
        run_text = run_path.read_text(encoding="utf-8")

        # Extract primary subcommand names from case branches in ./run
        case_branch_pattern = re.compile(r"^\s{2}([a-z0-9:_-]+(?:\|[a-z0-9:_-]+)*)\)\s*$", re.MULTILINE)
        commands: Set[str] = set()
        for match in case_branch_pattern.finditer(run_text):
            raw_branch = match.group(1)
            primary = raw_branch.split("|")[0].strip()
            if primary and not primary.startswith("-"):
                commands.add(primary)

        self.implemented_subcommands = commands

    def compare_cli_with_help_and_readme(self) -> None:
        res = subprocess.run(
            ["bash", str(self.workspace_root / "run"), "help"],
            cwd=self.workspace_root,
            capture_output=True,
            text=True,
            check=True,
        )
        self.help_output = res.stdout
        self.readme_content = (self.workspace_root / "README.md").read_text(encoding="utf-8")

    def assert_help_covers_subcommands(self) -> None:
        missing_in_help = [
            cmd for cmd in sorted(self.implemented_subcommands)
            if not re.search(rf"\b{re.escape(cmd)}\b", self.help_output)
        ]
        assert not missing_in_help, f"Subcommands implemented in ./run but missing from './run help': {missing_in_help}"

    def assert_readme_covers_subcommands(self) -> None:
        missing_in_readme = [
            cmd for cmd in sorted(self.implemented_subcommands)
            if f"./run {cmd}" not in self.readme_content
        ]
        assert not missing_in_readme, f"Subcommands implemented in ./run but missing from README.md: {missing_in_readme}"

    def load_workout_templates(self) -> None:
        templates_dir = self.workspace_root / "workouts" / "templates"
        self.template_names = sorted(p.stem for p in templates_dir.glob("*.txt"))
        assert len(self.template_names) > 0, "Expected workout templates in workouts/templates/"

    def assert_templates_documented(self) -> None:
        spec_text = (self.workspace_root / "docs" / "SYSTEM_SPEC.md").read_text(encoding="utf-8")
        skill_text = (self.workspace_root / ".agents" / "skills" / "workout-scheduler" / "SKILL.md").read_text(
            encoding="utf-8"
        )
        missing_spec = [t for t in self.template_names if f"`{t}`" not in spec_text]
        missing_skill = [t for t in self.template_names if f"`{t}`" not in skill_text]
        assert not missing_spec, f"Workout templates missing from docs/SYSTEM_SPEC.md: {missing_spec}"
        assert not missing_skill, f"Workout templates missing from workout-scheduler/SKILL.md: {missing_skill}"

    def validate_core_module_contracts(self) -> None:
        self.contract_errors.clear()

        # 1. Validate athlete profile contract
        athlete_path = self.workspace_root / "athletes" / "berni" / "athlete.json"
        profile_data = json.loads(athlete_path.read_text(encoding="utf-8"))
        ok_prof, errs_prof = validate_athlete_profile(profile_data)
        if not ok_prof:
            self.contract_errors.extend([f"athlete.json: {e}" for e in errs_prof])

        # 2. Validate 4-stage run review evaluation contract
        sample_run: Dict[str, Any] = {
            "id": "i999001",
            "type": "Run",
            "name": "Gerasdorf - Easy Run (45m)",
            "start_date_local": "2026-10-25T08:00:00",
            "distance": 6200.0,
            "moving_time": 2700,
            "average_heartrate": 148.0,
            "max_heartrate": 156,
            "average_cadence": 79.0,
            "total_elevation_gain": 25.0,
            "icu_training_load": 42.0,
            "icu_ctl": 18.5,
            "icu_atl": 20.0,
            "icu_hr_zone_times": [600, 1950, 150, 0, 0, 0, 0],
            "icu_intervals": ["1x 45m 148bpm"],
        }
        metrics = extract_deep_run_metrics(sample_run, athlete_profile=profile_data)
        evaluation = evaluate_run_deep(metrics, athlete_profile=profile_data, run_raw=sample_run)
        ok_rev, errs_rev = validate_run_review_evaluation(evaluation)
        if not ok_rev:
            self.contract_errors.extend([f"evaluate_run_deep: {e}" for e in errs_rev])

        # 3. Validate planning markers contract
        sample_wellness = [
            {
                "id": "2026-10-26",
                "ctl": 18.5,
                "atl": 20.0,
                "restingHR": 52,
                "hrv": 64.0,
                "sleepSecs": 27000,
                "sleepScore": 82,
            }
        ]
        markers = extract_planning_markers(
            activities_data=[sample_run],
            wellness_data=sample_wellness,
            profile_data=profile_data,
            ref_date=datetime.date(2026, 10, 26),
            target_date=datetime.date(2026, 10, 27),
        )
        ok_mark, errs_mark = validate_planning_markers(markers)
        if not ok_mark:
            self.contract_errors.extend([f"extract_planning_markers: {e}" for e in errs_mark])

        # 4. Validate coach history contract
        history_path = self.workspace_root / "athletes" / "berni" / "coach_history.json"
        history_data = json.loads(history_path.read_text(encoding="utf-8"))
        ok_hist, errs_hist = validate_coach_history(history_data)
        if not ok_hist:
            self.contract_errors.extend([f"coach_history.json: {e}" for e in errs_hist])

        # 5. Validate decision trace contract
        trace_entry = evaluate_workout_decision(markers, trigger="test_contract")
        ok_trace, errs_trace = validate_decision_trace_entry(trace_entry)
        if not ok_trace:
            self.contract_errors.extend([f"evaluate_workout_decision: {e}" for e in errs_trace])

    def assert_no_contract_errors(self) -> None:
        assert not self.contract_errors, (
            "Data contract validation errors detected:\n" + "\n".join(self.contract_errors)
        )


@pytest.fixture
def driver() -> DocumentationIntegrityDriver:
    return DocumentationIntegrityDriver(PROJECT_ROOT)


@given(parsers.parse("the active repository documentation files"))
def given_active_docs(driver: DocumentationIntegrityDriver) -> None:
    driver.load_active_docs()


@when(parsers.parse("all file links and workspace path references are inspected"))
def when_links_inspected(driver: DocumentationIntegrityDriver) -> None:
    driver.inspect_markdown_links()


@then(parsers.parse("every referenced file path exists in the workspace"))
def then_all_links_exist(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_no_broken_links()


@then(parsers.parse("no document contains absolute filesystem paths or file URI schemes"))
def then_no_legacy_mounts(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_no_legacy_mount_links()


@given(parsers.parse('the central CLI runner script "./run"'))
def given_cli_runner(driver: DocumentationIntegrityDriver) -> None:
    driver.load_cli_runner_subcommands()


@when(parsers.parse('implemented subcommands are compared against "./run help" and "README.md"'))
def when_cli_compared(driver: DocumentationIntegrityDriver) -> None:
    driver.compare_cli_with_help_and_readme()


@then(parsers.parse("every primary subcommand is documented in the help output"))
def then_help_covers_all(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_help_covers_subcommands()


@then(parsers.parse('every primary subcommand is documented in "README.md"'))
def then_readme_covers_all(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_readme_covers_subcommands()


@given(parsers.parse('the workout templates directory "workouts/templates"'))
def given_workout_templates(driver: DocumentationIntegrityDriver) -> None:
    driver.load_workout_templates()


@when(parsers.parse('template names are compared against "docs/SYSTEM_SPEC.md" and ".agents/skills/workout-scheduler/SKILL.md"'))
def when_templates_compared(driver: DocumentationIntegrityDriver) -> None:
    pass


@then(parsers.parse("every workout template is documented in both files"))
def then_templates_documented(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_templates_documented()


@when(parsers.parse("links to specification documents are inspected"))
def when_spec_links_inspected(driver: DocumentationIntegrityDriver) -> None:
    driver.inspect_markdown_links()


@then(parsers.parse('no active document links to unarchived "docs/plans/" paths'))
def then_no_unarchived_plans(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_no_unarchived_plan_links()


@given(parsers.parse('the executable data contracts in "src/core/contracts.py"'))
def given_contracts(driver: DocumentationIntegrityDriver) -> None:
    assert (driver.workspace_root / "src" / "core" / "contracts.py").is_file()


@when(parsers.parse("sample outputs from the 4-stage run reviewer, planning marker extractor, coach history, and athlete profile are validated"))
def when_outputs_validated(driver: DocumentationIntegrityDriver) -> None:
    driver.validate_core_module_contracts()


@then(parsers.parse("all outputs pass their respective contract validators without errors"))
def then_contracts_pass(driver: DocumentationIntegrityDriver) -> None:
    driver.assert_no_contract_errors()
