import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Scenarios from feature file
scenarios("recent_runs.feature")


# 2. Test Driver
class RecentRunsDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.input_file = tmp_path / "test_activities.json"
        self.process_result: subprocess.CompletedProcess | None = None

    def write_dataset(self, data: List[Dict[str, Any]]) -> None:
        with open(self.input_file, "w", encoding="utf-8") as f:
            json.dump(data, f)

    def prepare_dataset_with_runs(self) -> None:
        activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Morning Easy Run",
                "start_date_local": "2026-10-01T07:30:00",
                "distance": 6000.0,
                "moving_time": 2160,  # 36m (6:00/km)
                "average_heartrate": 142,
                "max_heartrate": 155,
                "icu_training_load": 45,
                "icu_hr_zone_times": [600, 1200, 300, 60, 0, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Tempo Threshold",
                "start_date_local": "2026-10-02T08:00:00",
                "distance": 7000.0,
                "moving_time": 2520,  # 42m (6:00/km)
                "average_heartrate": 166,
                "max_heartrate": 177,
                "icu_training_load": 73,
                "icu_hr_zone_times": [50, 50, 100, 600, 800, 800, 120],
            },
            {
                "id": "run-003",
                "type": "Run",
                "name": "Weekend Long Run",
                "start_date_local": "2026-10-04T09:00:00",
                "distance": 12000.0,
                "moving_time": 4500,  # 75m (6:15/km)
                "average_heartrate": 145,
                "max_heartrate": 160,
                "icu_training_load": 80,
                "icu_hr_zone_times": [1000, 2500, 800, 200, 0, 0, 0],
            },
        ]
        self.write_dataset(activities)

    def prepare_empty_dataset(self) -> None:
        activities = [
            {"id": "stub-01", "type": None, "source": "STRAVA"},
            {"id": "ride-01", "type": "Ride", "distance": 20000.0, "moving_time": 3000},
        ]
        self.write_dataset(activities)

    def run_recent_runs(self, extra_args: List[str] | None = None) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "analysis" / "recent_runs.py"),
            "--input",
            str(self.input_file),
        ]
        if extra_args:
            cmd.extend(extra_args)

        self.process_result = subprocess.run(
            cmd,
            cwd=self.workspace_root,
            capture_output=True,
            text=True,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected exit code {expected_code}, got {self.process_result.returncode}.\n"
            f"STDOUT:\n{self.process_result.stdout}\n"
            f"STDERR:\n{self.process_result.stderr}"
        )

    def assert_output_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout or "") + (self.process_result.stderr or "")
        assert text in combined, f"Expected '{text}' in output:\n{combined}"

    def assert_valid_json_with_runs(self) -> None:
        assert self.process_result is not None
        data = json.loads(self.process_result.stdout)
        assert "runs" in data
        assert len(data["runs"]) == 3
        first_run = data["runs"][0]
        # Newest run first (2026-10-04)
        assert first_run["name"] == "Weekend Long Run"
        assert "distance_km" in first_run
        assert "avg_pace" in first_run
        assert "avg_hr" in first_run
        assert "max_hr" in first_run
        assert "z1_z2_pct" in first_run

    def assert_json_runs_count(self, expected_count: int, flag: str) -> None:
        args = flag.split() + ["--json"]
        self.run_recent_runs(extra_args=args)
        assert self.process_result is not None
        data = json.loads(self.process_result.stdout)
        assert "runs" in data
        assert len(data["runs"]) == expected_count, (
            f"Expected {expected_count} runs, got {len(data['runs'])}"
        )


# 3. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path) -> RecentRunsDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return RecentRunsDriver(workspace_root, tmp_path)


# 4. Step Definitions
@given("an activities dataset with multiple recent runs")
def given_dataset_with_runs(driver: RecentRunsDriver):
    driver.prepare_dataset_with_runs()


@given("an activities dataset with no running activities")
def given_dataset_without_runs(driver: RecentRunsDriver):
    driver.prepare_empty_dataset()


@when("the recent runs script is executed")
def when_script_executed(driver: RecentRunsDriver):
    driver.run_recent_runs()


@when(parsers.parse('the recent runs script is executed with "{arg}"'))
def when_script_executed_with_arg(driver: RecentRunsDriver, arg: str):
    args = arg.split()
    driver.run_recent_runs(extra_args=args)


@then(parsers.parse("the process exits with code {code:d}"))
def then_process_exits_with_code(driver: RecentRunsDriver, code: int):
    driver.assert_exit_code(code)


@then("the stdout report displays the recent runs table")
def then_stdout_displays_table(driver: RecentRunsDriver):
    driver.assert_output_contains("RECENT RUNNING ACTIVITIES")
    driver.assert_output_contains("Date & Time")


@then("the report displays distance, duration, pace, heart rate, and zone percentages")
def then_report_displays_metrics(driver: RecentRunsDriver):
    driver.assert_output_contains("12.00 km")
    driver.assert_output_contains("6:15 /km")
    driver.assert_output_contains("Z1-Z2 %")


@then(parsers.parse('the output mentions "{keyword}"'))
def then_output_mentions_keyword(driver: RecentRunsDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then("the stdout contains valid JSON with a list of recent runs")
def then_stdout_contains_valid_json(driver: RecentRunsDriver):
    driver.assert_valid_json_with_runs()


@then(parsers.parse("exactly {count:d} runs are displayed in the JSON output"))
def then_exact_runs_count(driver: RecentRunsDriver, count: int):
    driver.assert_json_runs_count(count, "--limit 2")
