#!/usr/bin/env python3
"""BDD Step definitions and test driver for incremental per-run review and cache."""

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

from src.analysis.ai_coach import build_coach_prompt
from src.analysis.run_reviewer import (
    extract_deep_run_metrics,
    find_unreviewed_runs,
    load_run_reviews,
    review_runs,
    save_run_reviews,
)

# 1. Load scenarios from feature file
scenarios("run_reviewer.feature")


# 2. Local Test Driver
class RunReviewerDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.activities_file = tmp_path / "activities.json"
        self.athlete_file = tmp_path / "athlete.json"
        self.reviews_file = tmp_path / "run_reviews.json"

        self.sample_run: Dict[str, Any] = {}
        self.runs: List[Dict[str, Any]] = []
        self.extracted_metrics: Dict[str, Any] = {}
        self.reviews: Dict[str, Any] = {}
        self.coach_prompt: str = ""

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
        self.save_athlete_file()

    def save_athlete_file(self) -> None:
        self.athlete_file.write_text(
            json.dumps(self.athlete_profile, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    def save_activities(self) -> None:
        self.activities_file.write_text(
            json.dumps(self.runs, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    def setup_sample_activity(self) -> None:
        self.sample_run = {
            "id": "run-101",
            "type": "Run",
            "name": "Easy Base Run",
            "start_date_local": "2026-10-05T18:00:00",
            "distance": 6000.0,
            "moving_time": 2880,
            "average_heartrate": 149.0,
            "max_heartrate": 159.0,
            "average_cadence": 75.0,  # 75 RPM -> 150 SPM
            "total_elevation_gain": 25.0,
            "total_elevation_loss": 24.0,
            "icu_training_load": 42.0,
            "icu_hr_zone_times": [1200, 1600, 80, 0, 0, 0, 0],
            "interval_summary": ["4x 8m 148bpm"],
        }
        self.runs = [self.sample_run]
        self.save_activities()

    def setup_multiple_runs(self) -> None:
        self.runs = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Run Alpha",
                "start_date_local": "2026-10-01T10:00:00",
                "distance": 5000.0,
                "moving_time": 2000,
                "average_heartrate": 150.0,
                "max_heartrate": 160.0,
                "average_cadence": 75.0,
                "icu_hr_zone_times": [800, 1100, 100, 0, 0, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Run Beta",
                "start_date_local": "2026-10-03T10:00:00",
                "distance": 7000.0,
                "moving_time": 2600,
                "average_heartrate": 168.0,
                "max_heartrate": 178.0,
                "average_cadence": 78.0,
                "icu_hr_zone_times": [100, 200, 1000, 1300, 0, 0, 0],
            },
        ]
        self.save_activities()

    def extract_metrics(self) -> None:
        self.extracted_metrics = extract_deep_run_metrics(
            self.sample_run, self.athlete_profile
        )

    def generate_reviews(self, limit: int) -> None:
        self.reviews = review_runs(
            activities_file=self.activities_file,
            reviews_file=self.reviews_file,
            athlete_file=self.athlete_file,
            limit=limit,
        )

    def check_unreviewed_count(self) -> int:
        current_cache = load_run_reviews(self.reviews_file)
        unreviewed = find_unreviewed_runs(self.runs, current_cache)
        return len(unreviewed)

    def setup_existing_cache_with_reviews(self) -> None:
        self.setup_multiple_runs()
        self.generate_reviews(limit=2)

    def setup_zone3_run(self) -> None:
        self.sample_run = {
            "id": "run-z3",
            "type": "Run",
            "name": "Brisk Tempo Run",
            "start_date_local": "2026-10-06T18:00:00",
            "distance": 7000.0,
            "moving_time": 2400,
            "average_heartrate": 164.0,
            "max_heartrate": 172.0,
            "average_cadence": 82.0,
            "icu_training_load": 55.0,
            "icu_hr_zone_times": [200, 600, 1200, 400, 0, 0, 0],
        }
        self.runs = [self.sample_run]
        self.save_activities()

    def setup_threshold_run(self) -> None:
        self.sample_run = {
            "id": "run-threshold-1",
            "type": "Run",
            "name": "Wien - Schwellenlauf (7 km)",
            "start_date_local": "2026-10-23T07:00:00",
            "distance": 7150.0,
            "moving_time": 2996,
            "average_heartrate": 159.0,
            "max_heartrate": 176.0,
            "average_cadence": 76.4,
            "icu_training_load": 58.0,
            "icu_hr_zone_times": [378, 1112, 682, 827, 0, 0, 0],
            "interval_summary": ["3x 5m59s 168bpm", "2x 8m13s 157bpm"],
        }
        self.runs = [self.sample_run]
        self.save_activities()

    def setup_flexible_threshold_run(self) -> None:
        self.sample_run = {
            "id": "run-flex-1",
            "type": "Run",
            "name": "Morgenlauf",
            "start_date_local": "2026-10-23T07:00:00",
            "distance": 7000.0,
            "moving_time": 2800,
            "average_heartrate": 160.0,
            "max_heartrate": 175.0,
            "average_cadence": 77.0,
            "icu_training_load": 56.0,
            "icu_hr_zone_times": [300, 1000, 700, 800, 0, 0, 0],
        }
        self.runs = [self.sample_run]
        self.save_activities()

    def resolve_intent(self) -> None:
        self.extracted_metrics = extract_deep_run_metrics(self.sample_run, self.athlete_profile)
        from src.analysis.run_reviewer import resolve_run_intent
        self.resolved_intent = resolve_run_intent(self.sample_run, self.extracted_metrics, self.athlete_profile)

    def evaluate_deep_run(self) -> None:
        self.extracted_metrics = extract_deep_run_metrics(self.sample_run)
        from src.analysis.run_reviewer import evaluate_run_deep
        self.deep_review = evaluate_run_deep(self.extracted_metrics, self.athlete_profile, run_raw=self.sample_run)

    def build_prompt_with_cache(self) -> None:
        self.coach_prompt = build_coach_prompt(
            activities_file=self.activities_file,
            athlete_file=self.athlete_file,
            reviews_file=self.reviews_file,
            save_classification=False,
        )


# 3. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path) -> RunReviewerDriver:
    return RunReviewerDriver(tmp_path)


# 4. Step Definitions
@given("an activity with heart rate zone times, cadence, elevation, and intervals")
def given_activity_with_deep_data(driver: RunReviewerDriver) -> None:
    driver.setup_sample_activity()


@when("deep run metrics are extracted")
def when_deep_metrics_extracted(driver: RunReviewerDriver) -> None:
    driver.extract_metrics()


@then("the metrics contain exact seconds for all heart rate zones")
def then_metrics_contain_zone_seconds(driver: RunReviewerDriver) -> None:
    zb = driver.extracted_metrics.get("zone_breakdown", {})
    assert "z1" in zb and "z2" in zb and "z3" in zb
    assert zb["z1"]["seconds"] == 1200
    assert zb["z2"]["seconds"] == 1600
    assert zb["z3"]["seconds"] == 80


@then("the metrics contain cadence in strides per minute")
def then_metrics_contain_spm(driver: RunReviewerDriver) -> None:
    # 75 RPM doubled to 150 SPM
    assert driver.extracted_metrics["cadence_spm"] == 150.0


@then("the metrics identify the primary intensity classification")
def then_metrics_intensity_class(driver: RunReviewerDriver) -> None:
    assert driver.extracted_metrics["intensity_class"] == "aerobe_basis"


@given("a repository with running activities and an empty review cache")
def given_repo_with_runs_and_empty_cache(driver: RunReviewerDriver) -> None:
    driver.setup_multiple_runs()


@when(parsers.parse("run reviews are generated with a limit of {limit:d} runs"))
def when_run_reviews_generated(driver: RunReviewerDriver, limit: int) -> None:
    driver.generate_reviews(limit)


@then(parsers.parse("exactly {expected:d} run reviews are created and stored in the cache"))
def then_reviews_created(driver: RunReviewerDriver, expected: int) -> None:
    cached = load_run_reviews(driver.reviews_file)
    assert len(cached) == expected
    for rid in ("run-001", "run-002"):
        assert rid in cached
        assert "review" in cached[rid]
        assert "rating" in cached[rid]["review"]


@then("subsequent review execution detects zero unanalyzed runs")
def then_zero_unanalyzed_detected(driver: RunReviewerDriver) -> None:
    assert driver.check_unreviewed_count() == 0


@given("an existing review cache containing detailed reviews for recent runs")
def given_existing_cache_with_reviews(driver: RunReviewerDriver) -> None:
    driver.setup_existing_cache_with_reviews()


@when("an AI coach prompt is constructed")
def when_ai_coach_prompt_constructed(driver: RunReviewerDriver) -> None:
    driver.build_prompt_with_cache()


@then("the prompt includes deep metrics and cached review summaries for those runs")
def then_prompt_includes_deep_metrics_and_reviews(driver: RunReviewerDriver) -> None:
    assert "Detail-Zonen" in driver.coach_prompt
    assert "Einzel-Review" in driver.coach_prompt
    assert "Kadenz" in driver.coach_prompt


@given("an activity with high Zone 3 time and average heart rate above Zone 2")
def given_activity_high_zone3(driver: RunReviewerDriver) -> None:
    driver.setup_zone3_run()


@when("deep run metrics and review are evaluated")
def when_deep_run_metrics_and_review(driver: RunReviewerDriver) -> None:
    driver.evaluate_deep_run()


@then('the run rating is "zuegig"')
def then_run_rating_zuegig(driver: RunReviewerDriver) -> None:
    assert driver.deep_review.get("rating") == "zuegig"


@then('the rating label is "Zügiger Dauerlauf (Zone 3)"')
def then_rating_label_zuegig(driver: RunReviewerDriver) -> None:
    assert driver.deep_review.get("rating_label") == "Zügiger Dauerlauf (Zone 3)"


@then('the review summary does not contain "Intensitätsfalle" or "Grauzone"')
def then_summary_no_grauzone(driver: RunReviewerDriver) -> None:
    summary = driver.deep_review.get("summary", "")
    assert "Intensitätsfalle" not in summary
    assert "Grauzone" not in summary


@given("a planned threshold run with 13 minutes in Zone 4 and post-interval drift")
def given_planned_threshold_run(driver: RunReviewerDriver) -> None:
    driver.setup_threshold_run()


@then('the run rating is "intensiv"')
def then_run_rating_intensiv(driver: RunReviewerDriver) -> None:
    assert driver.deep_review.get("rating") == "intensiv"


@then('the review contains 4-stage evaluation with compliant status "erfüllt"')
def then_review_contains_4stages_erfuellt(driver: RunReviewerDriver) -> None:
    stages = driver.deep_review.get("stages", {})
    assert "stage1_physiology" in stages
    assert "stage2_compliance" in stages
    assert "stage3_optimizations" in stages
    assert "stage4_next_run" in stages
    assert stages["stage2_compliance"]["status"] == "erfüllt"


@then("the review proposes recovery directive for 48 hours")
def then_review_proposes_recovery(driver: RunReviewerDriver) -> None:
    stage4 = driver.deep_review.get("stages", {}).get("stage4_next_run", {})
    assert stage4.get("recovery_hours") == 48
    assert "48h" in stage4.get("directive", "") or "Erholung" in stage4.get("directive", "")


@given("an activity on a flexible mid-week slot with over 10 minutes in Zone 4")
def given_flexible_midweek_threshold(driver: RunReviewerDriver) -> None:
    driver.setup_flexible_threshold_run()


@when("run intent is resolved")
def when_run_intent_resolved(driver: RunReviewerDriver) -> None:
    driver.resolve_intent()


@then('the intended type is "threshold" and the planned slot is flexible threshold')
def then_intended_type_threshold(driver: RunReviewerDriver) -> None:
    intent = driver.resolved_intent
    assert intent.get("intended_type") == "threshold"
    assert "Schwellen-Option" in intent.get("planned_slot", "")
