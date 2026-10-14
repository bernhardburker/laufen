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

    def setup_pyramidal_slots(self) -> None:
        self.athlete_profile["athlete"]["training_mode"] = "pyramidal_hybrid"
        self.athlete_profile["athlete"]["weekly_slots"] = [
            {
                "slot": 1,
                "name": "Slot 1 (Wochenanfang)",
                "role": "quality",
                "allowed_days": ["monday", "tuesday"],
                "allowed_types": ["intervals", "tempo_run"],
                "preferred_type": "intervals",
                "requires_base_anchor": True,
            },
            {
                "slot": 2,
                "name": "Slot 2 (Wochenmitte)",
                "role": "quality",
                "allowed_days": ["wednesday", "thursday", "friday"],
                "allowed_types": ["tempo_run", "intervals"],
                "preferred_type": "tempo_run",
                "requires_base_anchor": True,
            },
            {
                "slot": 3,
                "name": "Slot 3 (Wochenende)",
                "role": "base_anchor",
                "allowed_days": ["saturday", "sunday"],
                "workout_type": "easy_run",
                "name_display": "Aerober Anker & Long Run (Slot 3)",
                "target_hr_max": 158,
                "is_mandatory_base": True,
            },
        ]

    def setup_four_slots(self) -> None:
        self.athlete_profile["athlete"]["training_mode"] = "pyramidal_hybrid"
        self.athlete_profile["athlete"]["weekly_slots"] = [
            {
                "slot": 1,
                "name": "Slot 1 (Dienstag)",
                "role": "quality",
                "allowed_days": ["tuesday"],
                "allowed_types": ["intervals", "tempo_run"],
            },
            {
                "slot": 2,
                "name": "Slot 2 (Donnerstag)",
                "role": "quality",
                "allowed_days": ["thursday"],
                "allowed_types": ["tempo_run", "intervals"],
            },
            {
                "slot": 3,
                "name": "Slot 3 (Sonntag)",
                "role": "base_anchor",
                "allowed_days": ["sunday"],
                "workout_type": "easy_run",
                "is_mandatory_base": True,
            },
            {
                "slot": 4,
                "name": "Slot 4 (Samstag)",
                "role": "recovery",
                "allowed_days": ["saturday"],
                "workout_type": "recovery_run",
                "duration_min": 30,
            },
        ]

    def setup_run_friday_threshold(self) -> None:
        self.runs = [
            {
                "id": "run-fri-thresh",
                "type": "Run",
                "name": "Threshold Run",
                "start_date_local": "2026-10-09T17:00:00",
                "distance": 6000.0,
                "moving_time": 2500,
                "average_heartrate": 170.0,
                "max_heartrate": 178.0,
                "icu_training_load": 35.0,
            }
        ]

    def setup_tempo_earlier(self) -> None:
        self.runs.append(
            {
                "id": "run-tempo-earlier",
                "type": "Run",
                "name": "Tempo Run",
                "workout_type": "tempo_run",
                "start_date_local": "2026-10-09T17:00:00",
                "distance": 6000.0,
                "moving_time": 2400,
                "average_heartrate": 169.0,
                "max_heartrate": 175.0,
                "icu_training_load": 48.0,
            }
        )

    def setup_run_thursday(self) -> None:
        # Thursday 2026-10-08
        self.runs = [
            {
                "id": "run-thu",
                "type": "Run",
                "name": "Threshold Run",
                "start_date_local": "2026-10-08T17:00:00",
                "distance": 6500.0,
                "moving_time": 2400,
                "average_heartrate": 168.0,
                "max_heartrate": 175.0,
                "icu_training_load": 48.0,
            }
        ]

    def setup_run_sunday(self) -> None:
        # Sunday 2026-10-11
        self.runs = [
            {
                "id": "run-sun",
                "type": "Run",
                "name": "Weekend Base Run",
                "start_date_local": "2026-10-11T09:00:00",
                "distance": 7000.0,
                "moving_time": 3000,
                "average_heartrate": 150.0,
                "max_heartrate": 158.0,
                "icu_training_load": 45.0,
            }
        ]

    def setup_run_friday_no_weekend(self) -> None:
        # Friday 2026-10-09 (no runs on Sat 10 or Sun 11)
        self.runs = [
            {
                "id": "run-fri",
                "type": "Run",
                "name": "Friday Run",
                "start_date_local": "2026-10-09T17:00:00",
                "distance": 6000.0,
                "moving_time": 2600,
                "average_heartrate": 152.0,
                "max_heartrate": 160.0,
                "icu_training_load": 40.0,
            }
        ]

    def setup_run_tuesday(self) -> None:
        # Sunday 2026-10-11 base run + Tuesday 2026-10-13 interval workout
        self.runs = [
            {
                "id": "run-sun-base",
                "type": "Run",
                "name": "Weekend Base Run",
                "start_date_local": "2026-10-11T09:00:00",
                "distance": 7000.0,
                "moving_time": 3000,
                "average_heartrate": 150.0,
                "max_heartrate": 158.0,
                "icu_training_load": 45.0,
            },
            {
                "id": "run-tue",
                "type": "Run",
                "name": "Interval Workout",
                "start_date_local": "2026-10-13T17:00:00",
                "distance": 5500.0,
                "moving_time": 2100,
                "average_heartrate": 165.0,
                "max_heartrate": 180.0,
                "icu_training_load": 44.0,
            },
        ]

    def set_reference_date(self, ref_date: date) -> None:
        self.reference_date = ref_date

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


@given("a runner with pyramidal hybrid weekly slots configured")
def given_runner_with_pyramidal_slots(driver: DailyRecommendationDriver) -> None:
    driver.setup_pyramidal_slots()


@given("the runner completed a threshold workout on Thursday")
def given_completed_threshold_thursday(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_thursday()


@given("the runner completed a Zone 2 long run on Sunday")
def given_completed_long_run_sunday(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_sunday()


@given("the runner's last run was on Friday without a weekend run")
def given_friday_no_weekend(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_friday_no_weekend()


@given("the runner completed an interval workout on Tuesday")
def given_completed_intervals_tuesday(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_tuesday()


@given("a runner with a four-slot weekly schedule configured")
def given_four_slot_runner(driver: DailyRecommendationDriver) -> None:
    driver.setup_four_slots()


@given("the runner completed a threshold workout on Friday")
def given_completed_threshold_friday(driver: DailyRecommendationDriver) -> None:
    driver.setup_run_friday_threshold()


@given("the runner completed a tempo run earlier")
def given_tempo_earlier(driver: DailyRecommendationDriver) -> None:
    driver.setup_tempo_earlier()


@when("the daily training recommendation is calculated for today")
def when_daily_recommendation_calculated(driver: DailyRecommendationDriver) -> None:
    driver.compute_daily_recommendation()


@when("the daily training recommendation is calculated for Saturday")
def when_calculated_for_saturday(driver: DailyRecommendationDriver) -> None:
    driver.set_reference_date(date(2026, 10, 10))
    driver.compute_daily_recommendation()


@when("the daily training recommendation is calculated for Tuesday")
def when_calculated_for_tuesday(driver: DailyRecommendationDriver) -> None:
    driver.set_reference_date(date(2026, 10, 13))
    driver.compute_daily_recommendation()


@when("the daily training recommendation is calculated for Monday")
def when_calculated_for_monday(driver: DailyRecommendationDriver) -> None:
    driver.set_reference_date(date(2026, 10, 12))
    driver.compute_daily_recommendation()


@when("the daily training recommendation is calculated for Thursday")
def when_calculated_for_thursday(driver: DailyRecommendationDriver) -> None:
    driver.set_reference_date(date(2026, 10, 15))
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


@then(parsers.parse("the recommended workout slot is {expected_slot:d}"))
def then_recommended_slot_is(driver: DailyRecommendationDriver, expected_slot: int) -> None:
    assert driver.recommendation.get("slot") == expected_slot


@then(parsers.parse('the recommended workout type is "{expected_type}"'))
def then_recommended_workout_type(driver: DailyRecommendationDriver, expected_type: str) -> None:
    assert driver.recommendation.get("workout_type") == expected_type


@then("the recommendation targets Zone 2 heart rate ceiling")
def then_targets_zone_2_ceiling(driver: DailyRecommendationDriver) -> None:
    assert driver.recommendation.get("suggested_target_hr") == 158


@then(parsers.parse('the recommendation badge indicates "{expected_badge}"'))
def then_badge_indicates(driver: DailyRecommendationDriver, expected_badge: str) -> None:
    assert expected_badge.lower() in driver.recommendation.get("badge", "").lower()


@then("the recommendation indicates a Zone 2 catch-up base run")
def then_indicates_catch_up(driver: DailyRecommendationDriver) -> None:
    assert driver.recommendation.get("is_catch_up") is True


@then(parsers.parse("the recommended target heart rate is {expected_hr:d}"))
def then_recommended_target_hr(driver: DailyRecommendationDriver, expected_hr: int) -> None:
    assert driver.recommendation.get("suggested_target_hr") == expected_hr


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
