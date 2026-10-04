import json
import os
from pathlib import Path
import subprocess
from typing import List, Optional

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind feature scenarios
scenarios("cli_runner.feature")


# 2. Test Driver
class CliRunnerDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.process_result: Optional[subprocess.CompletedProcess] = None
        self.test_data_dir = tmp_path / "data"
        self.test_data_dir.mkdir(parents=True, exist_ok=True)
        self.activities_file = self.test_data_dir / "intervals_activities.json"

    def prepare_dataset(self) -> None:
        activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Morning Z2",
                "start_date_local": "2026-10-01T07:00:00",
                "distance": 6000.0,
                "moving_time": 2160,
                "average_heartrate": 142,
                "icu_training_load": 45,
                "icu_hr_zone_times": [600, 1200, 300, 60, 0, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Tempo 5k",
                "start_date_local": "2026-10-03T18:00:00",
                "distance": 5000.0,
                "moving_time": 1600,
                "average_heartrate": 162,
                "icu_training_load": 55,
                "icu_hr_zone_times": [200, 400, 600, 400, 0, 0, 0],
            },
        ]
        with open(self.activities_file, "w", encoding="utf-8") as f:
            json.dump(activities, f)

    def run_command(self, args: Optional[List[str]] = None) -> None:
        cmd = ["bash", str(self.workspace_root / "run")]
        if args:
            cmd.extend(args)

        env = os.environ.copy()
        # Point to test activities file if command uses data
        if self.activities_file.exists():
            env["ACTIVITIES_PATH"] = str(self.activities_file)

        self.process_result = subprocess.run(
            cmd,
            cwd=self.workspace_root,
            env=env,
            capture_output=True,
            text=True,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected {expected_code}, got {self.process_result.returncode}.\n"
            f"STDOUT:\n{self.process_result.stdout}\n"
            f"STDERR:\n{self.process_result.stderr}"
        )

    def assert_exit_code_non_zero(self) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode != 0, f"Expected non-zero exit code: {self.process_result.stdout}"

    def assert_output_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout or "") + (self.process_result.stderr or "")
        assert text in combined, f"Expected '{text}' in output:\n{combined}"


# 3. Fixture
@pytest.fixture
def driver(tmp_path: Path) -> CliRunnerDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return CliRunnerDriver(workspace_root, tmp_path)


# 4. Step Definitions
@given("an activities dataset with multiple running activities")
def given_dataset(driver: CliRunnerDriver):
    driver.prepare_dataset()


@when(parsers.parse('the cli runner is executed with "{cmd_arg}"'))
def when_runner_with_arg(driver: CliRunnerDriver, cmd_arg: str):
    driver.run_command(args=cmd_arg.split())


@when("the cli runner is executed without arguments")
def when_runner_no_args(driver: CliRunnerDriver):
    driver.run_command(args=None)


@then(parsers.parse("the process exits with code {code:d}"))
def then_exits_code(driver: CliRunnerDriver, code: int):
    driver.assert_exit_code(code)


@then("the process exits with an error")
def then_exits_error(driver: CliRunnerDriver):
    driver.assert_exit_code_non_zero()


@then("the output contains the task runner overview")
def then_output_overview(driver: CliRunnerDriver):
    driver.assert_output_contains("Lauf- & Trainingsmanagement-Hub - Task Runner")
    driver.assert_output_contains("Usage: ./run <command>")


@then(parsers.parse('the output lists available commands like "{cmd1}", "{cmd2}", and "{cmd3}"'))
def then_output_lists_commands(driver: CliRunnerDriver, cmd1: str, cmd2: str, cmd3: str):
    driver.assert_output_contains(cmd1)
    driver.assert_output_contains(cmd2)
    driver.assert_output_contains(cmd3)


@then(parsers.parse('the error output mentions "{keyword}"'))
def then_error_mentions(driver: CliRunnerDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then(parsers.parse('the output contains "{keyword}"'))
def then_output_contains(driver: CliRunnerDriver, keyword: str):
    driver.assert_output_contains(keyword)
