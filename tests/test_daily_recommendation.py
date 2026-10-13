#!/usr/bin/env python3
"""BDD Step definitions and test driver for Daily Adaptive Training Recommendation."""

from datetime import date
import json
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from src.analysis.daily_recommendation import (
    generate_daily_recommendation,
    render_daily_recommendation_html,
)
from src.visualization.generate_dashboard import generate_dashboard

# 1. Load scenarios from feature file
scenarios("daily_recommendation.feature")


# 2. Local Test Driver
class DailyRecommendationDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.runs: List[Dict[str, Any]] = []
        self.athlete_profile: Dict[str, Any] = {
            "athlete": {
                "name": "Berni",
                "classification": "Hobbyläufer (Basisaufbau)",
                "heart_rate": {
                    "lthr": 178,
                    "max_hr": 204,
                    "resting_hr": 46,
                    "zones": {"z2_aerobic": {"max": 158}},
                },
            }
        }
        self.coach_history: List[Dict[str, Any]] = [
            {
                "id": "cycle-003",
                "date": "2026-10-08",
                "recommendation": "Nächsten Grundlagenlauf über 45-50 Minuten ansetzen.",
                "target_type": "easy_run",
                "target_hr_max": 158,
                "next_duration_min": 45,
                "status": "offen",
            }
        ]
        self.reference_date: date = date(2026, 10, 8)
        self.recommendation: Dict[str, Any] = {}
        self.dashboard_html: str = ""

        # Paths for dashboard test
        self.activities_file = tmp_path / "activities.json"
        self.template_file = PROJECT_ROOT / "templates" / "dashboard.html"
        self.output_file = tmp_path / "index.html"

    def setup_run_today(self) -> None:
        self.reference_date = date(2026, 10, 8)
        self.runs = [
            {
                "id": "run-today",
                "type": "Run",
                "name": "Morning Run",
                "start_date_local": "2026-10-08T07:00:00",
                "distance": 7000.0,
                "moving_time": 3000,
                "average_heartrate": 155.0,
                "max_heartrate": 164.0,
                "icu_training_load": 53.0,
                "icu_hr_zone_times": [300, 1500, 1200, 0, 0, 0, 0],
            }
        ]

    def setup_run_two_days_ago(self) -> None:
        self.reference_date = date(2026, 10, 10)
        self.runs = [
            {
                "id": "run-past",
                "type": "Run",
                "name": "Base Run",
                "start_date_local": "2026-10-08T07:00:00",
                "distance": 6000.0,
                "moving_time": 2800,
                "average_heartrate": 150.0,
                "max_heartrate": 159.0,
                "icu_training_load": 45.0,
                "icu_hr_zone_times": [1000, 1800, 0, 0, 0, 0, 0],
            }
        ]

    def setup_run_eight_days_ago(self) -> None:
        self.reference_date = date(2026, 10, 16)
        self.runs = [
            {
                "id": "run-old",
                "type": "Run",
                "name": "Old Run",
                "start_date_local": "2026-10-08T07:00:00",
                "distance": 6000.0,
                "moving_time": 2800,
                "average_heartrate": 150.0,
                "icu_training_load": 45.0,
                "icu_hr_zone_times": [1000, 1800, 0, 0, 0, 0, 0],
            }
        ]

    def compute_daily_recommendation(self) -> None:
        self.recommendation = generate_daily_recommendation(
            runs=self.runs,
            athlete_profile=self.athlete_profile,
            coach_history=self.coach_history,
            reference_date=self.reference_date,
            form_data={"current_tsb": 2.5, "latest_ctl": 15.0, "latest_atl": 12.5},
        )

    def render_dashboard(self) -> None:
        self.activities_file.write_text(
            json.dumps(self.runs, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        generate_dashboard(
            input_file=self.activities_file,
            template_file=self.template_file,
            output_file=self.output_file,
            athlete_file=None,
        )
        self.dashboard_html = self.output_file.read_text(encoding="utf-8")


# 3. Fixture
@pytest.fixture
def driver(tmp_path: Path) -> DailyRecommendationDriver:
    return DailyRecommendationDriver(tmp_path)


# 4. Step Definitions
@given("a runner who completed a workout earlier today with significant training load")
def given_runner_completed_today(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_today()


@given("a runner whose last run was two days ago with restored positive form")
def given_runner_last_run_two_days_ago(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_two_days_ago()


@given("a runner whose last activity was eight days ago")
def given_runner_last_run_eight_days_ago(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_eight_days_ago()


@when("the daily training recommendation is calculated for today")
def when_daily_recommendation_calculated(driver: DailyRecommendationDriver) -> None:
    driver.compute_daily_recommendation()


@then(parsers.parse('the recommendation type is "{expected_type}"'))
def then_recommendation_type_is(driver: DailyRecommendationDriver, expected_type: str) -> None:
    assert driver.recommendation["action_type"] == expected_type


@then("the recommendation advises recovery and processing the training stimulus")
def then_advises_recovery(driver: DailyRecommendationDriver) -> None:
    details = driver.recommendation["details"].lower()
    assert "erholung" in details or "ruhe" in details
    assert driver.recommendation["suggested_duration_min"] == 0


@then("the recommendation advises executing the planned aerobic base workout")
def then_advises_workout(driver: DailyRecommendationDriver) -> None:
    assert driver.recommendation["suggested_duration_min"] >= 40
    assert driver.recommendation["suggested_target_hr"] == 158


@then("the recommended duration is reduced to 25-30 minutes for safe re-entry")
def then_duration_reduced(driver: DailyRecommendationDriver) -> None:
    assert 20 <= driver.recommendation["suggested_duration_min"] <= 30
    assert "wiedereinstieg" in driver.recommendation["headline"].lower() or "pause" in driver.recommendation["headline"].lower()


@given('a daily recommendation with type "rest_day"')
def given_daily_rec_rest_day(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_today()
    driver.compute_daily_recommendation()


@when("the HTML dashboard is generated")
def when_html_dashboard_generated(driver: DailyRecommendationDriver) -> None:
    driver.render_dashboard()


@then("the dashboard contains a Daily Recommendation card with appropriate action badge")
def then_dashboard_contains_daily_card(driver: DailyRecommendationDriver) -> None:
    assert '<div class="card daily-card">' in driver.dashboard_html
    assert "Empfehlung für heute" in driver.dashboard_html
