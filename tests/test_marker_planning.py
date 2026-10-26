#!/usr/bin/env python3
"""BDD Step definitions and test driver for 4-Stage Marker-Based Adaptive Workout Planning."""

import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from src.planner.extract_markers import extract_planning_markers
from src.planner.schedule_workout import choose_adaptive_workout

# 1. Load scenarios from feature file
scenarios("marker_planning.feature")


# 2. Local Test Driver
class MarkerPlanningDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.ref_date = datetime.date(2026, 10, 26)  # Monday
        self.target_date = datetime.date(2026, 10, 27)  # Tuesday
        self.athlete_profile: Dict[str, Any] = {
            "athlete": {
                "name": "Berni",
                "classification": "Hobbyläufer (Basisaufbau)",
                "heart_rate": {
                    "lthr": 178,
                    "max_hr": 204,
                    "resting_hr": 48,
                    "zones": {
                        "z1_recovery": {"max": 150},
                        "z2_aerobic": {"min": 151, "max": 158},
                        "z3_tempo": {"min": 159, "max": 168},
                        "z4_subthreshold": {"min": 169, "max": 177},
                    },
                },
                "weekly_slots": [
                    {
                        "slot": 1,
                        "name": "Slot 1 (Wochenanfang)",
                        "allowed_days": ["monday", "tuesday"],
                        "allowed_types": ["intervals", "tempo_run"],
                        "preferred_type": "intervals",
                        "description": "Tempo- oder Intervalltraining (Speed / Hügel / Fahrtspiel)",
                    },
                    {
                        "slot": 2,
                        "name": "Slot 2 (Wochenmitte)",
                        "allowed_days": ["wednesday", "thursday", "friday"],
                        "allowed_types": ["tempo_run", "easy_run"],
                        "preferred_type": "tempo_run",
                        "description": "Aerobe Basis (Z2) oder Schwellenlauf (Z4) bei hoher Frische",
                    },
                    {
                        "slot": 3,
                        "name": "Slot 3 (Wochenende)",
                        "allowed_days": ["saturday", "sunday"],
                        "allowed_types": ["easy_run"],
                        "description": "Lockerer Grundlagenlauf strictly in Zone 2",
                    },
                ],
            }
        }
        self.wellness: List[Dict[str, Any]] = []
        self.activities: List[Dict[str, Any]] = []
        self.markers_result: Dict[str, Any] = {}
        self.selected_workout: Optional[tuple[str, str]] = None
        self.cli_result: Optional[subprocess.CompletedProcess] = None

    def setup_athlete(self, rhr_baseline: int, z2_max: int) -> None:
        self.athlete_profile["athlete"]["heart_rate"]["resting_hr"] = rhr_baseline
        self.athlete_profile["athlete"]["heart_rate"]["zones"]["z2_aerobic"]["max"] = z2_max

    def setup_wellness(self, sleep_hours: float, tsb: float) -> None:
        rec_date = self.ref_date.isoformat()
        # Derive ctl and atl from tsb: ctl = 14.0, atl = ctl - tsb
        ctl = 14.0
        atl = round(ctl - tsb, 1)
        self.wellness.append({
            "id": rec_date,
            "ctl": ctl,
            "atl": atl,
            "restingHR": 49,
            "hrv": 67.0,
            "sleepSecs": int(sleep_hours * 3600),
            "sleepScore": 70.0 if sleep_hours < 6.0 else 85.0,
        })

    def add_run(
        self,
        days_ago: int,
        duration_min: float,
        name: str,
        is_quality: bool,
        z4_sec: int = 0,
    ) -> None:
        run_date = (self.ref_date - datetime.timedelta(days=days_ago)).isoformat()
        z_times = [
            int(duration_min * 10),
            int(duration_min * 45),
            int(duration_min * 5),
            z4_sec,
            0,
        ]
        self.activities.append({
            "start_date_local": f"{run_date}T08:00:00",
            "name": name,
            "type": "Run",
            "moving_time": int(duration_min * 60),
            "distance": int(duration_min * 135),
            "average_heartrate": 159.0 if is_quality else 151.0,
            "icu_training_load": 65.0 if duration_min > 50 else 40.0,
            "icu_hr_zone_times": z_times,
            "workout_type": "intervals" if "interval" in name.lower() else ("tempo" if is_quality else "easy"),
        })

    def extract_markers(self, target_date: Optional[datetime.date] = None) -> None:
        target = target_date or self.target_date
        self.markers_result = extract_planning_markers(
            activities_data=self.activities,
            wellness_data=self.wellness,
            profile_data=self.athlete_profile,
            ref_date=self.ref_date,
            target_date=target,
            project_root=PROJECT_ROOT,
        )
        templates_dir = PROJECT_ROOT / "workouts" / "templates"
        days_since = self.markers_result.get("history", {}).get("days_since_last_run", 1)
        self.selected_workout = choose_adaptive_workout(
            activities=self.activities,
            athlete_profile=self.athlete_profile,
            ref_date=self.ref_date,
            days_since_last_run=days_since,
            templates_dir=templates_dir,
            target_date=target,
            wellness_records=self.wellness,
            project_root=PROJECT_ROOT,
        )

    def assert_guardrail(self, name: str, active: bool) -> None:
        guardrails = self.markers_result.get("guardrails", {})
        actual = bool(guardrails.get(name, False))
        assert actual == active, f"Guardrail '{name}' expected {active}, got {actual}. Guardrails: {guardrails}"

    def assert_workout_is_easy_base(self) -> None:
        assert self.selected_workout is not None
        w_name, _ = self.selected_workout
        assert any(k in w_name.lower() for k in ("easy", "anker", "grundlage", "erholung")), (
            f"Expected easy/base workout, got: '{w_name}'"
        )

    def assert_workout_is_not_threshold(self) -> None:
        assert self.selected_workout is not None
        w_name, _ = self.selected_workout
        assert "schwelle" not in w_name.lower() and "threshold" not in w_name.lower(), (
            f"Expected non-threshold workout, got: '{w_name}'"
        )

    def assert_workout_name_equals(self, expected: str) -> None:
        assert self.selected_workout is not None
        w_name, _ = self.selected_workout
        assert expected == w_name, f"Expected '{expected}', got '{w_name}'"

    def run_cli_markers(self, args: List[str]) -> None:
        cmd = [
            str(PROJECT_ROOT / ".venv" / "bin" / "python"),
            str(PROJECT_ROOT / "src" / "planner" / "schedule_workout.py"),
            *args,
        ]
        env = os.environ.copy()
        env["PYTHONPATH"] = str(PROJECT_ROOT)
        self.cli_result = subprocess.run(
            cmd,
            cwd=str(PROJECT_ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=env,
        )

    def assert_cli_json_valid(self, required_keys: List[str]) -> None:
        assert self.cli_result is not None
        assert self.cli_result.returncode == 0, f"Stderr: {self.cli_result.stderr}"
        data = json.loads(self.cli_result.stdout)
        for k in required_keys:
            assert k in data, f"Key '{k}' not found in CLI output: {data.keys()}"


# 3. Fixtures
@pytest.fixture
def driver(tmp_path: Path) -> MarkerPlanningDriver:
    return MarkerPlanningDriver(tmp_path)


# 4. Step Definitions
@given(parsers.parse('an athlete with resting HR baseline of {rhr:d} bpm and max Z2 of {z2_max:d} bpm'))
def step_setup_athlete(driver: MarkerPlanningDriver, rhr: int, z2_max: int):
    driver.setup_athlete(rhr, z2_max)


@given(parsers.parse('wellness recorded with {sleep:f} hours sleep and TSB of {tsb:f}'))
def step_setup_wellness(driver: MarkerPlanningDriver, sleep: float, tsb: float):
    driver.setup_wellness(sleep, tsb)


@given(parsers.parse('a long run of {duration:d} minutes occurred yesterday'))
def step_add_yesterday_long_run(driver: MarkerPlanningDriver, duration: int):
    driver.add_run(days_ago=1, duration_min=float(duration), name="Long Run", is_quality=False)


@given(parsers.parse('a threshold run occurred {days_ago:d} days ago'))
def step_add_threshold_run(driver: MarkerPlanningDriver, days_ago: int):
    driver.add_run(days_ago=days_ago, duration_min=50.0, name="Schwellenlauf", is_quality=True, z4_sec=900)


@given(parsers.parse('an easy run occurred yesterday'))
def step_add_yesterday_easy_run(driver: MarkerPlanningDriver):
    driver.add_run(days_ago=1, duration_min=35.0, name="Easy Run", is_quality=False)


@given(parsers.parse('a fresh athlete with TSB of {tsb:f} and {sleep:f} hours sleep'))
def step_fresh_athlete(driver: MarkerPlanningDriver, tsb: float, sleep: float):
    driver.setup_wellness(sleep_hours=sleep, tsb=tsb)


@given(parsers.parse('the last quality run occurred {days_ago:d} days ago'))
def step_last_quality_days(driver: MarkerPlanningDriver, days_ago: int):
    driver.add_run(days_ago=days_ago, duration_min=45.0, name="Intervals Quality", is_quality=True, z4_sec=700)


@given(parsers.parse('the last run occurred {days_ago:d} days ago'))
def step_last_run_days(driver: MarkerPlanningDriver, days_ago: int):
    driver.add_run(days_ago=days_ago, duration_min=40.0, name="Base Run", is_quality=False)


@given(parsers.parse('the target day is a Tuesday matching Slot 1'))
def step_target_day_slot1(driver: MarkerPlanningDriver):
    driver.target_date = datetime.date(2026, 10, 27)


@when('planning markers are extracted for tomorrow')
def step_extract_tomorrow(driver: MarkerPlanningDriver):
    driver.extract_markers(driver.ref_date + datetime.timedelta(days=1))


@when('planning markers are extracted for the target day')
def step_extract_target_day(driver: MarkerPlanningDriver):
    driver.extract_markers(driver.target_date)


@then(parsers.parse('the guardrail "{name}" is active'))
def step_guardrail_active(driver: MarkerPlanningDriver, name: str):
    driver.assert_guardrail(name, True)


@then(parsers.parse('the guardrail "{name}" is inactive'))
def step_guardrail_inactive(driver: MarkerPlanningDriver, name: str):
    driver.assert_guardrail(name, False)


@then('the selected adaptive workout is an easy base run')
def step_workout_is_easy_base(driver: MarkerPlanningDriver):
    driver.assert_workout_is_easy_base()


@then('the selected adaptive workout is not a threshold workout')
def step_workout_is_not_threshold(driver: MarkerPlanningDriver):
    driver.assert_workout_is_not_threshold()


@then('no fatigue or quality guardrails are active')
def step_no_fatigue_or_quality_active(driver: MarkerPlanningDriver):
    driver.assert_guardrail("fatigue_warning", False)
    driver.assert_guardrail("quality_discouraged", False)


@then(parsers.parse('the periodization slot is "{expected_slot}"'))
def step_check_periodization_slot(driver: MarkerPlanningDriver, expected_slot: str):
    slot_name = driver.markers_result.get("periodization", {}).get("slot_name", "")
    assert expected_slot == slot_name, f"Expected slot '{expected_slot}', got '{slot_name}'"


@then(parsers.parse('the selected adaptive workout is "{expected_name}"'))
def step_check_selected_workout(driver: MarkerPlanningDriver, expected_name: str):
    driver.assert_workout_name_equals(expected_name)


@when(parsers.parse('the planner CLI is executed with "{arg1}" and "{arg2}"'))
def step_run_planner_cli(driver: MarkerPlanningDriver, arg1: str, arg2: str):
    driver.run_cli_markers([arg1, arg2])


@then(parsers.parse('the process exits with code {code:d}'))
def step_process_exits_code(driver: MarkerPlanningDriver, code: int):
    assert driver.cli_result is not None
    assert driver.cli_result.returncode == code, f"Stderr: {driver.cli_result.stderr}"


@then(parsers.parse('the stdout is valid JSON containing "{key1}" and "{key2}"'))
def step_check_cli_json(driver: MarkerPlanningDriver, key1: str, key2: str):
    driver.assert_cli_json_valid([key1, key2])
