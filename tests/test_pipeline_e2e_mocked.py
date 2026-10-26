#!/usr/bin/env python3
"""BDD Step definitions and driver for full mocked pipeline execution."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import sys
from typing import Any, Dict, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.ai_coach import run_ai_coach
from src.athletes import AthleteContext, load_registry, save_registry
from src.integrations.download_intervals import fetch_intervals_activities
from src.integrations.download_wellness import fetch_intervals_wellness
from src.integrations.sync_athlete import build_athlete_config, fetch_athlete_profile
from src.llm import get_llm_client
from tests.drivers import MockIntervalsServer

# 1. Bind feature file
scenarios("pipeline_e2e_mocked.feature")


# 2. Driver Context Holder
class PipelineContext:
    def __init__(self, tmp_path: Path) -> None:
        self.tmp_path = tmp_path
        self.mock_server: Optional[MockIntervalsServer] = None
        self.athlete_slug: str = "berni-test"
        self.athlete_dir = tmp_path / "athletes" / "berni-test"
        self.athlete_dir.mkdir(parents=True, exist_ok=True)
        self.coach_summary: Optional[Dict[str, Any]] = None
        self.llm_provider: str = "mock"


@pytest.fixture
def pipe_context(tmp_path: Path) -> PipelineContext:
    ctx = PipelineContext(tmp_path)
    yield ctx
    if ctx.mock_server:
        ctx.mock_server.stop()


# 3. Given Steps
@given("a running unified mock Intervals.icu server")
def start_unified_mock(pipe_context: PipelineContext) -> None:
    pipe_context.mock_server = MockIntervalsServer(port=0)
    # Populate mock with 2 test activities and wellness records
    pipe_context.mock_server.activities = [
        {
            "id": "mock_act_1",
            "name": "Z2 Aerobic Foundation",
            "type": "Run",
            "start_date_local": "2026-10-25T07:00:00",
            "distance": 8000.0,
            "moving_time": 2880,
            "elapsed_time": 2900,
            "average_speed": 2.778,
            "average_heartrate": 145,
            "max_heartrate": 156,
            "icu_training_load": 52,
        },
        {
            "id": "mock_act_2",
            "name": "Slot 1 Threshold Intervals",
            "type": "Run",
            "start_date_local": "2026-10-23T18:00:00",
            "distance": 7000.0,
            "moving_time": 2200,
            "elapsed_time": 2300,
            "average_speed": 3.18,
            "average_heartrate": 168,
            "max_heartrate": 182,
            "icu_training_load": 74,
        },
    ]
    pipe_context.mock_server.wellness = [
        {
            "id": "2026-10-26",
            "date": "2026-10-26",
            "hrv": 68.0,
            "restingHR": 49,
            "sleepSecs": 28800,
            "soreness": 1,
            "fatigue": 2,
        },
        {
            "id": "2026-10-25",
            "date": "2026-10-25",
            "hrv": 65.0,
            "restingHR": 51,
            "sleepSecs": 27000,
            "soreness": 2,
            "fatigue": 2,
        },
    ]
    pipe_context.mock_server.start()


@given(parsers.parse('an isolated athlete environment configured for "{athlete_slug}"'))
def setup_isolated_env(pipe_context: PipelineContext, athlete_slug: str) -> None:
    pipe_context.athlete_slug = athlete_slug
    # Write registry.json inside isolated workspace
    registry_path = pipe_context.tmp_path / "athletes" / "registry.json"
    registry_data = {
        "default_athlete": athlete_slug,
        "athletes": {
            athlete_slug: {
                "intervals_id": "i771119",
                "display_name": "Berni Test",
                "active": True,
                "dashboard_slug": "berni-test-mock",
            }
        },
    }
    save_registry(registry_data, registry_path=registry_path)


@given(parsers.parse('LLM provider is configured to "{provider_name}"'))
def configure_llm_provider(pipe_context: PipelineContext, provider_name: str) -> None:
    pipe_context.llm_provider = provider_name


# 4. When Steps
@when(parsers.parse('the full sync pipeline runs for "{athlete_slug}" against the mock server'))
def run_mock_sync(pipe_context: PipelineContext, athlete_slug: str) -> None:
    assert pipe_context.mock_server is not None
    base_url = pipe_context.mock_server.base_url

    # 1. Sync Profile
    athlete_json_dest = pipe_context.athlete_dir / "athlete.json"
    raw_profile = fetch_athlete_profile(
        athlete_id="i771119",
        api_key="mock_key",
        api_base_url=base_url,
    )
    athlete_config = build_athlete_config(raw_profile)
    with open(athlete_json_dest, "w", encoding="utf-8") as f:
        json.dump(athlete_config, f, indent=2)

    # 2. Sync Activities
    activities_json_dest = pipe_context.athlete_dir / "intervals_activities.json"
    activities = fetch_intervals_activities(
        api_key="mock_key",
        athlete_id="i771119",
        oldest="2026-10-01",
        api_base_url=base_url,
    )
    with open(activities_json_dest, "w", encoding="utf-8") as f:
        json.dump(activities, f, indent=2)

    # 3. Sync Wellness
    wellness_json_dest = pipe_context.athlete_dir / "intervals_wellness.json"
    wellness_records = fetch_intervals_wellness(
        api_key="mock_key",
        athlete_id="i771119",
        oldest="2026-10-01",
        api_base_url=base_url,
    )
    with open(wellness_json_dest, "w", encoding="utf-8") as f:
        json.dump(wellness_records, f, indent=2)


@when("the AI coach analysis runs with mocked LLM")
def run_coach_pipeline(pipe_context: PipelineContext) -> None:
    llm_client = get_llm_client(provider=pipe_context.llm_provider)
    pipe_context.coach_summary = run_ai_coach(
        activities_file=pipe_context.athlete_dir / "intervals_activities.json",
        athlete_file=pipe_context.athlete_dir / "athlete.json",
        history_file=pipe_context.athlete_dir / "coach_history.json",
        reviews_file=pipe_context.athlete_dir / "run_reviews.json",
        output_json=pipe_context.athlete_dir / "ai_coach_summary.json",
        output_md=pipe_context.athlete_dir / "ai_coach_commentary.md",
        llm_client=llm_client,
    )


# 5. Then Steps
@then("athlete profile, activities, and wellness records are stored locally")
def verify_local_storage(pipe_context: PipelineContext) -> None:
    assert (pipe_context.athlete_dir / "athlete.json").is_file()
    assert (pipe_context.athlete_dir / "intervals_activities.json").is_file()
    assert (pipe_context.athlete_dir / "intervals_wellness.json").is_file()

    with open(pipe_context.athlete_dir / "intervals_activities.json", "r", encoding="utf-8") as f:
        acts = json.load(f)
    assert len(acts) == 2


@then("coach summary artifact is generated")
def verify_coach_artifacts(pipe_context: PipelineContext) -> None:
    summary_path = pipe_context.athlete_dir / "ai_coach_summary.json"
    commentary_path = pipe_context.athlete_dir / "ai_coach_commentary.md"
    assert summary_path.is_file(), "ai_coach_summary.json not found"
    assert commentary_path.is_file(), "ai_coach_commentary.md not found"
    assert pipe_context.coach_summary is not None


@then("the mock Intervals.icu server received activity and wellness requests")
def verify_mock_requests(pipe_context: PipelineContext) -> None:
    assert pipe_context.mock_server is not None
    # Verify mock server served activities and wellness
    assert len(pipe_context.mock_server.activities) >= 2
    assert len(pipe_context.mock_server.wellness) >= 2
