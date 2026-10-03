import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Scenarios from feature file
scenarios("trends.feature")


# 2. Test Driver
class TrendsAnalysisDriver:
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
                "name": "Week 36 Easy Run",
                "start_date_local": "2026-09-02T07:00:00",
                "distance": 6000.0,
                "moving_time": 2160,  # 36m (6:00/km)
                "average_heartrate": 140,
                "icu_training_load": 45,
                "icu_hr_zone_times": [600, 1200, 300, 60, 0, 0, 0],  # 1800s in Z1+Z2
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Week 36 Long Run",
                "start_date_local": "2026-09-06T08:30:00",
                "distance": 10000.0,
                "moving_time": 3600,  # 60m (6:00/km)
                "average_heartrate": 145,
                "icu_training_load": 75,
                "icu_hr_zone_times": [600, 2400, 500, 100, 0, 0, 0],  # 3000s in Z1+Z2
            },
            {
                "id": "run-003",
                "type": "Run",
                "name": "Week 37 Tempo Run",
                "start_date_local": "2026-09-09T18:00:00",
                "distance": 8000.0,
                "moving_time": 2560,  # 5:20/km
                "average_heartrate": 160,
                "icu_training_load": 65,
                "icu_hr_zone_times": [200, 300, 1000, 800, 260, 0, 0],
            },
        ]
        self.write_dataset(activities)

    def prepare_empty_dataset(self) -> None:
        activities = [
            {"id": "stub-01", "type": None, "source": "STRAVA"},
            {"id": "ride-01", "type": "Ride", "distance": 20000.0, "moving_time": 3000},
        ]
        self.write_dataset(activities)

    def run_trends(self, extra_args: List[str] | None = None) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "analysis" / "trends.py"),
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

    def assert_valid_json_with_metrics(self) -> None:
        assert self.process_result is not None
        data = json.loads(self.process_result.stdout)
        assert "trends" in data
        assert len(data["trends"]) >= 2
        first_week = data["trends"][0]
        assert "iso_week" in first_week
        assert "total_distance_km" in first_week
        assert "avg_pace" in first_week
        assert "z1_z2_pct" in first_week
        assert "aerobic_ef" in first_week


# 3. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path) -> TrendsAnalysisDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return TrendsAnalysisDriver(workspace_root, tmp_path)


# 4. Step Definitions
@given("an activities dataset with runs across multiple weeks")
def given_dataset_with_runs(driver: TrendsAnalysisDriver):
    driver.prepare_dataset_with_runs()


@given("an activities dataset with no running activities")
def given_dataset_without_runs(driver: TrendsAnalysisDriver):
    driver.prepare_empty_dataset()


@when("the trend analysis script is executed")
def when_script_executed(driver: TrendsAnalysisDriver):
    driver.run_trends()


@when(parsers.parse('the trend analysis script is executed with "{arg}"'))
def when_script_executed_with_arg(driver: TrendsAnalysisDriver, arg: str):
    driver.run_trends(extra_args=[arg])


@then(parsers.parse("the process exits with code {code:d}"))
def then_process_exits_with_code(driver: TrendsAnalysisDriver, code: int):
    driver.assert_exit_code(code)


@then("the stdout report displays the weekly trends table")
def then_stdout_displays_table(driver: TrendsAnalysisDriver):
    driver.assert_output_contains("WEEKLY RUNNING TRENDS")
    driver.assert_output_contains("Week")


@then("the report contains weekly totals for distance, time, and training load")
def then_report_contains_totals(driver: TrendsAnalysisDriver):
    driver.assert_output_contains("16.00 km")  # 6km + 10km for week 36
    driver.assert_output_contains("Load")


@then("the report displays Zone 1 and 2 percentage and aerobic efficiency")
def then_report_displays_zones_and_ef(driver: TrendsAnalysisDriver):
    driver.assert_output_contains("Z1-Z2 %")
    driver.assert_output_contains("EF")


@then(parsers.parse('the output mentions "{keyword}"'))
def then_output_mentions_keyword(driver: TrendsAnalysisDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then("the stdout contains valid JSON with weekly metrics")
def then_stdout_contains_valid_json(driver: TrendsAnalysisDriver):
    driver.assert_valid_json_with_metrics()
