#!/usr/bin/env python3
"""BDD Step definitions and driver for subjective run notes management."""

from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any, Dict, List, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.integrations.manage_note import attach_note_to_run
from tests.drivers import MockIntervalsServer

scenarios("manage_note.feature")


class ManageNoteDriver:
    def __init__(self, tmp_path: Path) -> None:
        self.tmp_path = tmp_path
        self.athlete_dir = tmp_path / "athlete"
        self.athlete_dir.mkdir(parents=True, exist_ok=True)
        self.activities_path = self.athlete_dir / "intervals_activities.json"
        self.reviews_path = self.athlete_dir / "run_reviews.json"
        self.profile_path = self.athlete_dir / "athlete.json"
        self.history_path = self.athlete_dir / "coach_history.json"

        self.mock_server: Optional[MockIntervalsServer] = None
        self.initial_activities_snapshot: List[Dict[str, Any]] = []
        self.last_result: Optional[Dict[str, Any]] = None
        self.cli_process_result: Optional[subprocess.CompletedProcess] = None

    def setup_athlete(self) -> None:
        profile_data = {
            "athlete": {"name": "Test Runner", "resting_hr": 48, "max_hr": 190, "lthr": 170},
            "hr_zones": {
                "z1_recovery": [0, 135],
                "z2_aerobic": [136, 152],
                "z3_tempo": [153, 163],
                "z4_threshold": [164, 172],
                "z5_vo2max": [173, 190],
            },
            "target_runs_per_week": 3,
            "weekly_slots": [
                {"slot": 1, "day_of_week": "Tuesday", "focus": "Intervals"},
                {"slot": 2, "day_of_week": "Thursday", "focus": "Tempo"},
                {"slot": 3, "day_of_week": "Saturday", "focus": "Long Run"},
            ],
        }
        self.profile_path.write_text(json.dumps(profile_data, indent=2), encoding="utf-8")
        self.history_path.write_text(json.dumps([], indent=2), encoding="utf-8")

        activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Tempo Session",
                "start_date_local": "2026-10-20T07:00:00",
                "distance": 8000.0,
                "moving_time": 2400,
                "average_heartrate": 155,
                "icu_training_load": 50,
                "icu_hr_zone_times": [300, 900, 1000, 200, 0, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Latest Aerobic Run",
                "start_date_local": "2026-10-25T08:30:00",
                "distance": 10000.0,
                "moving_time": 3300,
                "average_heartrate": 145,
                "icu_training_load": 65,
                "icu_hr_zone_times": [300, 2400, 600, 0, 0, 0, 0],
            },
        ]
        self.activities_path.write_text(json.dumps(activities, indent=2), encoding="utf-8")
        self.initial_activities_snapshot = copy.deepcopy(activities)

    def setup_run_reviews(self) -> None:
        initial_reviews = {
            "run-001": {
                "id": "run-001",
                "name": "Tempo Session",
                "athlete_note": "",
                "review": {"stages": {"stage2_compliance": {"status": "compliant"}}},
            }
        }
        self.reviews_path.write_text(json.dumps(initial_reviews, indent=2), encoding="utf-8")

    def attach_note(
        self,
        note_text: str,
        activity_id: Optional[str] = None,
        feel: Optional[int] = None,
        rpe: Optional[int] = None,
        dry_run: bool = False,
    ) -> None:
        api_key = "mock_key" if self.mock_server else None
        api_base = f"{self.mock_server.base_url}/api/v1" if self.mock_server else "https://intervals.icu/api/v1"

        self.last_result = attach_note_to_run(
            note_text=note_text,
            activities_path=self.activities_path,
            reviews_path=self.reviews_path,
            activity_id=activity_id,
            feel=feel,
            perceived_exertion=rpe,
            api_key=api_key,
            api_base_url=api_base,
            athlete_profile_path=self.profile_path,
            coach_history_path=self.history_path,
            dry_run=dry_run,
        )

    def run_cli_note(self, note_text: str) -> None:
        cmd = [
            "bash",
            str(PROJECT_ROOT / "run"),
            "note",
            note_text,
            "--activities-file",
            str(self.activities_path),
            "--reviews-file",
            str(self.reviews_path),
            "--athlete-config",
            str(self.profile_path),
            "--coach-history",
            str(self.history_path),
        ]
        env = os.environ.copy()
        env["INTERVALS_API_KEY"] = ""

        self.cli_process_result = subprocess.run(
            cmd,
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
            env=env,
        )


@pytest.fixture
def driver(tmp_path: Path):
    d = ManageNoteDriver(tmp_path)
    yield d
    if d.mock_server:
        d.mock_server.stop()


@given("an athlete with recorded running activities")
def given_athlete(driver: ManageNoteDriver) -> None:
    driver.setup_athlete()


@given("a running Intervals.icu mock server")
def given_mock_server(driver: ManageNoteDriver) -> None:
    server = MockIntervalsServer()
    server.start()
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        server.activities = json.load(f)
    driver.mock_server = server


@given("an existing run reviews cache")
def given_existing_reviews(driver: ManageNoteDriver) -> None:
    driver.setup_run_reviews()


@when(parsers.parse('note "{note_text}" with feel {feel:d} is attached to the latest run'))
def when_attach_note_to_latest(driver: ManageNoteDriver, note_text: str, feel: int) -> None:
    driver.attach_note(note_text=note_text, feel=feel)


@when(parsers.parse('note "{note_text}" with RPE {rpe:d} is attached to activity "{activity_id}"'))
def when_attach_note_to_id(driver: ManageNoteDriver, note_text: str, rpe: int, activity_id: str) -> None:
    driver.attach_note(note_text=note_text, activity_id=activity_id, rpe=rpe)


@when(parsers.parse('dry run mode is used to attach note "{note_text}" to the latest run'))
def when_dry_run_attach(driver: ManageNoteDriver, note_text: str) -> None:
    driver.attach_note(note_text=note_text, dry_run=True)


@when(parsers.parse('note "{note_text}" is attached to the latest run'))
def when_attach_note_reeval(driver: ManageNoteDriver, note_text: str) -> None:
    driver.attach_note(note_text=note_text)


@when(parsers.parse('attempting to attach note "{note_text}" to non-existent activity "{activity_id}"'))
def when_attach_nonexistent(driver: ManageNoteDriver, note_text: str, activity_id: str) -> None:
    driver.attach_note(note_text=note_text, activity_id=activity_id)


@when(parsers.parse('the CLI runner is executed with note "{note_text}"'))
def when_cli_runner_note(driver: ManageNoteDriver, note_text: str) -> None:
    driver.run_cli_note(note_text=note_text)


@then(parsers.parse('the latest activity contains athlete note "{expected_note}"'))
def then_latest_activity_contains_note(driver: ManageNoteDriver, expected_note: str) -> None:
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        activities = json.load(f)
    latest = sorted(activities, key=lambda a: a["start_date_local"], reverse=True)[0]
    assert latest.get("athlete_note") == expected_note
    assert latest.get("description") == expected_note


@then(parsers.parse("the latest activity records feel {expected_feel:d}"))
def then_latest_activity_feel(driver: ManageNoteDriver, expected_feel: int) -> None:
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        activities = json.load(f)
    latest = sorted(activities, key=lambda a: a["start_date_local"], reverse=True)[0]
    assert latest.get("feel") == expected_feel


@then(parsers.parse("the Intervals.icu server receives the updated note for the latest run"))
def then_intervals_receives_update(driver: ManageNoteDriver) -> None:
    assert driver.mock_server is not None
    matching = [a for a in driver.mock_server.activities if a.get("id") == "run-002"]
    assert matching, "Target activity not found in mock server activities"
    assert matching[0].get("feel") == 3


@then(parsers.parse('activity "{activity_id}" contains athlete note "{expected_note}"'))
def then_activity_contains_note(driver: ManageNoteDriver, activity_id: str, expected_note: str) -> None:
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        activities = json.load(f)
    matching = [a for a in activities if a["id"] == activity_id]
    assert matching
    assert matching[0].get("athlete_note") == expected_note


@then(parsers.parse("activity \"{activity_id}\" records perceived exertion {expected_rpe:d}"))
def then_activity_records_rpe(driver: ManageNoteDriver, activity_id: str, expected_rpe: int) -> None:
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        activities = json.load(f)
    matching = [a for a in activities if a["id"] == activity_id]
    assert matching
    assert matching[0].get("perceived_exertion") == expected_rpe


@then("local activities file remains unchanged")
def then_activities_file_unchanged(driver: ManageNoteDriver) -> None:
    with open(driver.activities_path, "r", encoding="utf-8") as f:
        current_activities = json.load(f)
    assert current_activities == driver.initial_activities_snapshot


@then("the Intervals.icu server receives no activity update")
def then_intervals_no_update(driver: ManageNoteDriver) -> None:
    assert driver.mock_server is not None
    latest = [a for a in driver.mock_server.activities if a.get("id") == "run-002"][0]
    assert "description" not in latest or not latest["description"]


@then("the run reviews cache contains an updated review for the latest run")
def then_reviews_cache_updated(driver: ManageNoteDriver) -> None:
    with open(driver.reviews_path, "r", encoding="utf-8") as f:
        reviews = json.load(f)
    assert "run-002" in reviews


@then(parsers.parse('the updated review includes the athlete note "{expected_note}"'))
def then_review_includes_note(driver: ManageNoteDriver, expected_note: str) -> None:
    with open(driver.reviews_path, "r", encoding="utf-8") as f:
        reviews = json.load(f)
    review = reviews["run-002"]
    assert review.get("athlete_note") == expected_note


@then("the process aborts with an activity not found error")
def then_activity_not_found_error(driver: ManageNoteDriver) -> None:
    assert driver.last_result is not None
    assert driver.last_result.get("status") == "error"
    assert "not found" in driver.last_result.get("message", "").lower()


@then("the CLI runner exits successfully")
def then_cli_runner_success(driver: ManageNoteDriver) -> None:
    assert driver.cli_process_result is not None
    assert driver.cli_process_result.returncode == 0, (
        f"CLI failed with {driver.cli_process_result.returncode}:\n"
        f"{driver.cli_process_result.stdout}\n{driver.cli_process_result.stderr}"
    )
