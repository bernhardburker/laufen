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

from src.analysis.forecast import (
    calculate_fitness_form_trend,
    generate_training_summary,
    generate_volume_forecast,
    predict_race_times,
)
from src.analysis.trends import aggregate_weekly_trends, filter_runs

# 1. Scenarios from feature file
scenarios("forecast.feature")


# 2. Test Driver
class ForecastDriver:
    def __init__(self, workspace_root: Path):
        self.workspace_root = workspace_root
        self.runs: List[Dict[str, Any]] = []
        self.trends: List[Dict[str, Any]] = []
        self.athlete_profile: Dict[str, Any] = {}
        self.predictions: List[Dict[str, Any]] = []
        self.volume_forecast: List[Dict[str, Any]] = []
        self.form_data: Dict[str, Any] = {}
        self.summary: Dict[str, Any] = {}

    def prepare_dataset_with_runs(self) -> None:
        self.runs = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Tempo 7k",
                "start_date_local": "2026-10-01T07:00:00",
                "distance": 7000.0,
                "moving_time": 2520,  # 6:00/km
                "average_heartrate": 162,
                "icu_training_load": 65,
                "icu_ctl": 15.0,
                "icu_atl": 18.0,
                "icu_hr_zone_times": [100, 200, 500, 1200, 520, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Long 10k",
                "start_date_local": "2026-10-03T09:00:00",
                "distance": 10000.0,
                "moving_time": 3900,  # 6:30/km
                "average_heartrate": 155,
                "icu_training_load": 80,
                "icu_ctl": 15.8,
                "icu_atl": 22.0,
                "icu_hr_zone_times": [300, 600, 1500, 1500, 0, 0, 0],
            },
        ]
        self.athlete_profile = {
            "athlete": {
                "heart_rate": {
                    "lthr": 166,
                    "zones": {"z2_aerobic": {"max": 148}},
                }
            }
        }
        self.trends = aggregate_weekly_trends(self.runs)

    def compute_predictions(self) -> None:
        self.predictions = predict_race_times(self.runs, self.athlete_profile)

    def compute_volume_forecast(self, weeks: int = 4) -> None:
        self.volume_forecast = generate_volume_forecast(self.trends, num_weeks=weeks)

    def compute_summary(self) -> None:
        self.summary = generate_training_summary(
            self.runs, self.trends, self.athlete_profile
        )

    def assert_standard_predictions_returned(self) -> None:
        assert len(self.predictions) == 4
        names = [p["name"] for p in self.predictions]
        assert "5 km" in names
        assert "10 km" in names
        assert "Halbmarathon" in names
        assert "Marathon" in names

    def assert_prediction_details(self) -> None:
        for p in self.predictions:
            assert "/km" in p["target_pace_str"]
            assert len(p["predicted_time_str"]) > 0
            assert len(p["readiness"]) > 0
            assert p["predicted_seconds"] > 0

    def assert_volume_targets_count(self, expected_count: int) -> None:
        assert len(self.volume_forecast) == expected_count
        for vf in self.volume_forecast:
            assert "iso_week" in vf
            assert vf["projected_distance_km"] > 0
            assert vf["is_forecast"] is True

    def assert_progression_with_deload(self) -> None:
        # Check that at least one week is labeled recovery/deload
        descriptions = [
            (vf.get("phase_title", "") + " " + vf.get("description", "")).lower()
            for vf in self.volume_forecast
        ]
        has_deload = any(
            "regenerat" in d or "deload" in d or "erholung" in d for d in descriptions
        )
        assert has_deload, "Expected at least one deload/recovery week in forecast."

    def assert_summary_structure(self) -> None:
        assert "status_title" in self.summary
        assert "status_badge" in self.summary
        assert len(self.summary.get("insights", [])) >= 2

    def assert_actionable_recommendations(self) -> None:
        recs = self.summary.get("recommendations", [])
        assert len(recs) >= 2
        titles = [r["title"].lower() for r in recs]
        # Should include zone discipline advice or volume advice
        has_zone_or_pace_advice = any(
            "pace" in t or "zone" in t or "dauerläuf" in t or "reiz" in t for t in titles
        )
        assert has_zone_or_pace_advice, "Expected zone/pace advice in recommendations."


# 3. Pytest Fixture
@pytest.fixture
def driver() -> ForecastDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return ForecastDriver(workspace_root)


# 4. Step Definitions
@given("an activities dataset with multiple running activities")
def given_activities_dataset(driver: ForecastDriver):
    driver.prepare_dataset_with_runs()


@given("weekly training trends with recent running activities")
def given_weekly_trends(driver: ForecastDriver):
    driver.prepare_dataset_with_runs()


@when("race time predictions are calculated")
def when_calculate_predictions(driver: ForecastDriver):
    driver.compute_predictions()


@when(parsers.parse("training volume forecast is generated for {weeks:d} weeks"))
def when_generate_volume_forecast(driver: ForecastDriver, weeks: int):
    driver.compute_volume_forecast(weeks=weeks)


@when("training summary and recommendations are generated")
def when_generate_summary(driver: ForecastDriver):
    driver.compute_summary()


@then("race predictions are returned for standard distances")
def then_check_predictions_returned(driver: ForecastDriver):
    driver.assert_standard_predictions_returned()


@then("each prediction includes target pace, predicted time, and readiness note")
def then_check_prediction_details(driver: ForecastDriver):
    driver.assert_prediction_details()


@then(parsers.parse("{count:d} progressive weekly volume targets are produced"))
def then_check_volume_targets_count(driver: ForecastDriver, count: int):
    driver.assert_volume_targets_count(count)


@then("the progression includes a deload or recovery week")
def then_check_deload_week(driver: ForecastDriver):
    driver.assert_progression_with_deload()


@then("the summary identifies training status and key observations")
def then_check_summary_structure(driver: ForecastDriver):
    driver.assert_summary_structure()


@then("actionable recommendations are provided for zone discipline and volume")
def then_check_recommendations(driver: ForecastDriver):
    driver.assert_actionable_recommendations()
