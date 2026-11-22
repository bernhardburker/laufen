from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List
from unittest.mock import patch

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from src.analysis.run_reviewer import review_runs
from src.analysis.zone_pace import compute_run_zone_pace_stats
from src.integrations.download_intervals import sync_run_streams

scenarios("zone_pace.feature")


class ZonePaceDriver:
    def __init__(self, tmp_path: Path) -> None:
        self.tmp_path = tmp_path
        self.profile: Dict[str, Any] = {}
        self.stream: Dict[str, Any] = {}
        self.stats: Dict[str, Any] = {}
        self.activities_file: Path = tmp_path / "intervals_activities.json"
        self.reviews_file: Path = tmp_path / "run_reviews.json"
        self.streams_dir: Path = tmp_path / "streams"
        self.fetched_ids: List[str] = []
        self.updated_reviews: Dict[str, Any] = {}

    def set_profile(self, z2_min: int, z2_max: int, z3_min: int, z3_max: int) -> None:
        self.profile = {
            "athlete": {
                "heart_rate": {
                    "lthr": 178,
                    "zones": {
                        "z1": {"zone": 1, "name": "Zone 1", "min": 0, "max": z2_min - 1},
                        "z2_aerobic": {"zone": 2, "name": "Zone 2", "min": z2_min, "max": z2_max},
                        "z3": {"zone": 3, "name": "Zone 3", "min": z3_min, "max": z3_max},
                        "z4": {"zone": 4, "name": "Zone 4", "min": z3_max + 1, "max": 177},
                        "z5": {"zone": 5, "name": "Zone 5", "min": 178, "max": 192},
                    },
                }
            }
        }

    def set_stream_segments(self, segments: List[tuple[int, int, float]]) -> None:
        hrs: List[int] = []
        speeds: List[float] = []
        times: List[int] = []
        alts: List[float] = []
        t = 0
        for count, hr, spd in segments:
            for _ in range(count):
                times.append(t)
                hrs.append(hr)
                speeds.append(spd)
                alts.append(160.0)
                t += 1
        self.stream = {
            "time": times,
            "heartrate": hrs,
            "velocity_smooth": speeds,
            "fixed_altitude": alts,
        }

    def compute_stats(self) -> None:
        self.stats = compute_run_zone_pace_stats(self.stream, self.profile)

    def setup_incremental_review_workspace(self) -> None:
        self.streams_dir.mkdir(parents=True, exist_ok=True)
        acts = [
            {
                "id": "run_cached",
                "type": "Run",
                "start_date_local": "2026-11-10T08:00:00",
                "name": "Already Analyzed Run",
                "distance": 5000.0,
                "moving_time": 2100,
                "average_heartrate": 152,
                "max_heartrate": 160,
                "icu_hr_zone_times": [200, 1900, 0, 0, 0],
            },
            {
                "id": "run_missing_zp",
                "type": "Run",
                "start_date_local": "2026-11-12T08:00:00",
                "name": "Needs Zone Pace",
                "distance": 5000.0,
                "moving_time": 2000,
                "average_heartrate": 154,
                "max_heartrate": 162,
                "icu_hr_zone_times": [100, 1900, 0, 0, 0],
            },
        ]
        self.activities_file.write_text(json.dumps(acts), encoding="utf-8")
        stream_payload = {
            "time": list(range(150)),
            "heartrate": [155] * 150,
            "velocity_smooth": [2.4] * 150,
            "fixed_altitude": [160.0] * 150,
        }
        for rid in ("run_cached", "run_missing_zp"):
            (self.streams_dir / f"{rid}.json").write_text(json.dumps(stream_payload), encoding="utf-8")

        initial_reviews = {
            "run_cached": {
                "id": "run_cached",
                "date": "2026-11-10T08:00:00",
                "name": "Already Analyzed Run",
                "zone_pace_stats": {"has_streams": True, "zones": {}},
                "review": {"schema_version": 2},
                "analyzed_at": "2026-11-10T09:00:00",
            },
            "run_missing_zp": {
                "id": "run_missing_zp",
                "date": "2026-11-12T08:00:00",
                "name": "Needs Zone Pace",
                "review": {"schema_version": 2},
                "analyzed_at": "2026-11-12T09:00:00",
            },
        }
        self.reviews_file.write_text(json.dumps(initial_reviews), encoding="utf-8")

    def run_incremental_review(self) -> None:
        self.updated_reviews = review_runs(
            activities_file=self.activities_file,
            reviews_file=self.reviews_file,
            streams_dir=self.streams_dir,
        )

    def run_mocked_stream_sync(self) -> None:
        self.streams_dir.mkdir(parents=True, exist_ok=True)
        (self.streams_dir / "act_cached.json").write_text('{"time":[0]}', encoding="utf-8")
        activities = [
            {"id": "act_cached", "type": "Run", "average_heartrate": 150},
            {"id": "act_new", "type": "Run", "average_heartrate": 155},
        ]

        def fake_fetch(activity_id: str, **_kwargs: Any) -> Dict[str, Any]:
            self.fetched_ids.append(activity_id)
            return {"time": [0, 1], "heartrate": [150, 152], "velocity_smooth": [2.3, 2.4]}

        with patch("src.integrations.download_intervals.fetch_activity_streams", side_effect=fake_fetch):
            sync_run_streams(activities, self.streams_dir, api_key="dummy-key")


@pytest.fixture
def driver(tmp_path: Path) -> ZonePaceDriver:
    return ZonePaceDriver(tmp_path)


@given(parsers.parse("an athlete profile with Zone 2 from {z2_min:d} to {z2_max:d} bpm and Zone 3 from {z3_min:d} to {z3_max:d} bpm"))
def given_profile(driver: ZonePaceDriver, z2_min: int, z2_max: int, z3_min: int, z3_max: int) -> None:
    driver.set_profile(z2_min, z2_max, z3_min, z3_max)


@given(parsers.parse("an activity stream with {z2_s:d} seconds in Zone 2 at {z2_v:f} m/s, {walk_s:d} seconds walking in Zone 2 at {walk_v:f} m/s, and {z3_s:d} seconds in Zone 3 at {z3_v:f} m/s"))
def given_stream_with_walk(
    driver: ZonePaceDriver, z2_s: int, z2_v: float, walk_s: int, walk_v: float, z3_s: int, z3_v: float
) -> None:
    driver.set_stream_segments([(z2_s, 155, z2_v), (walk_s, 153, walk_v), (z3_s, 163, z3_v)])


@given(parsers.parse("an activity stream with {h1_s:d} seconds slow first half at {h1_v:f} m/s and {h2_s:d} seconds fast second half at {h2_v:f} m/s"))
def given_split_stream(driver: ZonePaceDriver, h1_s: int, h1_v: float, h2_s: int, h2_v: float) -> None:
    driver.set_stream_segments([(h1_s, 152, h1_v), (h2_s, 165, h2_v)])


@when("I compute the zone pace statistics for the stream")
def when_compute(driver: ZonePaceDriver) -> None:
    driver.compute_stats()


@then(parsers.parse("{walk_s:d} walking seconds are filtered out"))
def then_walking_filtered(driver: ZonePaceDriver, walk_s: int) -> None:
    assert driver.stats["walking_seconds_filtered"] == walk_s


@then(parsers.parse('Zone 2 is valid for trends with {sec:d} running seconds and GAP pace around "{expected_pace}"'))
def then_z2_valid(driver: ZonePaceDriver, sec: int, expected_pace: str) -> None:
    z2 = driver.stats["zones"]["z2"]
    assert z2["valid_for_trend"] is True
    assert z2["running_seconds"] == sec
    assert z2["gap_pace_str"] == expected_pace


@then(parsers.parse("Zone 3 has {sec:d} running seconds and is marked not valid for trends"))
def then_z3_invalid(driver: ZonePaceDriver, sec: int) -> None:
    z3 = driver.stats["zones"]["z3"]
    assert z3["running_seconds"] == sec
    assert z3["valid_for_trend"] is False


@then(parsers.parse("the first-half GAP pace is slower than the second-half GAP pace by at least {diff_s:d} seconds per km"))
def then_split_diff(driver: ZonePaceDriver, diff_s: int) -> None:
    assert (driver.stats["first_half_gap_pace_s_per_km"] - driver.stats["second_half_gap_pace_s_per_km"]) >= diff_s


@given("an activities file with two runs where cached streams exist for both")
def given_two_runs_with_streams(driver: ZonePaceDriver) -> None:
    driver.setup_incremental_review_workspace()


@given("one run already has zone_pace_stats in run_reviews while the other run is missing it")
def given_partial_reviews(driver: ZonePaceDriver) -> None:
    pass


@when("I run incremental review_runs on the workspace")
def when_run_incremental_review(driver: ZonePaceDriver) -> None:
    driver.run_incremental_review()


@then("only the run missing zone_pace_stats is re-analyzed")
def then_only_missing_updated(driver: ZonePaceDriver) -> None:
    assert driver.updated_reviews["run_missing_zp"].get("zone_pace_stats", {}).get("has_streams") is True


@then("the already-analyzed run preserves its original analyzed_at timestamp")
def then_cached_preserved(driver: ZonePaceDriver) -> None:
    assert driver.updated_reviews["run_cached"]["analyzed_at"] == "2026-11-10T09:00:00"


@given("two run activities where one stream is already cached locally")
def given_one_cached_stream(driver: ZonePaceDriver) -> None:
    pass


@when("I run sync_run_streams with a mocked Intervals.icu API")
def when_sync_streams(driver: ZonePaceDriver) -> None:
    driver.run_mocked_stream_sync()


@then("only the missing run stream is fetched and saved")
def then_only_missing_fetched(driver: ZonePaceDriver) -> None:
    assert driver.fetched_ids == ["act_new"]
    assert (driver.streams_dir / "act_new.json").is_file()
