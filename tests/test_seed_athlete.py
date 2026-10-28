#!/usr/bin/env python3
"""BDD Step definitions and driver for synthetic test data seeder."""

from __future__ import annotations

from pathlib import Path
import os
import subprocess
import sys
from typing import Any, Dict, List
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.integrations.seed_athlete import generate_wellness_records

# 1. Bind feature file
scenarios("seed_athlete.feature")


# 2. Driver Context
class SeederDriver:
    def __init__(self) -> None:
        self.athlete_slug: str = "berni-test"
        self.wellness_records: List[Dict[str, Any]] = []
        self.process_result: Optional[subprocess.CompletedProcess] = None


@pytest.fixture
def driver() -> SeederDriver:
    return SeederDriver()


# 3. Given Steps
@given(parsers.parse('target athlete is "{athlete_slug}"'))
def set_target_athlete(driver: SeederDriver, athlete_slug: str) -> None:
    driver.athlete_slug = athlete_slug


# 4. When Steps
@when(parsers.parse('synthetic wellness records are generated for {days:d} days under "{scenario}" scenario'))
def generate_records(driver: SeederDriver, days: int, scenario: str) -> None:
    driver.wellness_records = generate_wellness_records(days=days, scenario=scenario)


@when("seed athlete process is attempted on the production athlete")
def attempt_production_seed(driver: SeederDriver) -> None:
    cmd = [
        sys.executable,
        "-m",
        "src.integrations.seed_athlete",
        "--athlete",
        driver.athlete_slug,
    ]
    env = os.environ.copy()
    env["INTERVALS_API_KEY"] = "mock_key"
    driver.process_result = subprocess.run(
        cmd,
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True,
        env=env,
    )


# 5. Then Steps
@then(parsers.parse("{expected_count:d} daily wellness records are returned"))
def verify_record_count(driver: SeederDriver, expected_count: int) -> None:
    assert len(driver.wellness_records) == expected_count


@then(parsers.parse("average HRV is within normal range between {hrv_min:d} and {hrv_max:d}"))
def verify_hrv_range(driver: SeederDriver, hrv_min: int, hrv_max: int) -> None:
    hrv_values = [r["hrv"] for r in driver.wellness_records if "hrv" in r]
    assert hrv_values, "No HRV records found"
    avg_hrv = sum(hrv_values) / len(hrv_values)
    assert hrv_min <= avg_hrv <= hrv_max, f"Expected avg HRV between {hrv_min} and {hrv_max}, got {avg_hrv}"


@then("the process aborts with a safety violation error")
def verify_safety_abort(driver: SeederDriver) -> None:
    assert driver.process_result is not None
    assert driver.process_result.returncode != 0
    assert "SAFETY ABORT" in (driver.process_result.stderr + driver.process_result.stdout)
