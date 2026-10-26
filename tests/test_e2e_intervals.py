#!/usr/bin/env python3
"""BDD Step definitions and driver for live Intervals.icu E2E API integration."""

from __future__ import annotations

import datetime
import os
from pathlib import Path
import sys
from typing import Any, Dict, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.athletes import resolve_athlete_context
from tests.drivers import LiveIntervalsDriver

# 1. Bind feature file with e2e pytest marker
scenarios("e2e_intervals.feature")


# 2. Driver Context Holder
class E2ETestContext:
    def __init__(self) -> None:
        self.api_key: Optional[str] = None
        self.athlete_id: str = "i771119"
        self.driver: Optional[LiveIntervalsDriver] = None
        self.profile_response: Dict[str, Any] = {}
        self.created_event: Dict[str, Any] = {}
        self.events_list: list[Dict[str, Any]] = []


@pytest.fixture
def context() -> E2ETestContext:
    ctx = E2ETestContext()
    yield ctx
    if ctx.driver:
        ctx.driver.cleanup_created_events()


# 3. Given Steps
@given("a configured Intervals.icu API key")
def check_api_key(context: E2ETestContext) -> None:
    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        env_file = PROJECT_ROOT / ".env"
        if env_file.is_file():
            from src.integrations.upload_activities import load_env_file

            load_env_file(env_file)
            api_key = os.getenv("INTERVALS_API_KEY")

    if not api_key:
        pytest.skip("INTERVALS_API_KEY not configured in environment or .env; skipping live E2E test")
    context.api_key = api_key


@given(parsers.parse('target staging athlete is "{athlete_slug}"'))
def resolve_staging_athlete(context: E2ETestContext, athlete_slug: str) -> None:
    athlete_ctx = resolve_athlete_context(athlete_slug, project_root=PROJECT_ROOT)
    assert athlete_ctx.intervals_id != "0", f"Athlete '{athlete_slug}' has no valid intervals_id"
    context.athlete_id = athlete_ctx.intervals_id
    context.driver = LiveIntervalsDriver(
        api_key=context.api_key or "",
        athlete_id=context.athlete_id,
    )


# 4. When Steps
@when("the athlete profile is retrieved from the live Intervals.icu API")
def fetch_profile(context: E2ETestContext) -> None:
    assert context.driver is not None
    context.profile_response = context.driver.get_athlete_profile()


@when("a test workout event is uploaded to the live staging calendar")
def upload_test_workout(context: E2ETestContext) -> None:
    assert context.driver is not None
    today_str = datetime.date.today().isoformat()
    # Create structured event with clear test signature
    payload = {
        "category": "WORKOUT",
        "type": "Run",
        "name": "[TEST-E2E] Automated Verification Workout",
        "description": "Created by automated BDD E2E test suite. Safe to delete.",
        "start_date_local": f"{today_str}T09:00:00",
        "moving_time": 1800,
    }
    context.created_event = context.driver.create_workout_event(payload)


# 5. Then Steps
@then(parsers.parse('the response contains the athlete id "{expected_id}"'))
def verify_athlete_id(context: E2ETestContext, expected_id: str) -> None:
    assert context.profile_response.get("id") == expected_id, (
        f"Expected athlete id {expected_id}, got {context.profile_response.get('id')}"
    )


@then(parsers.parse("the response contains heart rate zones with LTHR greater than {lthr_min:d}"))
def verify_lthr(context: E2ETestContext, lthr_min: int) -> None:
    lthr = None
    sport_settings = context.profile_response.get("sportSettings") or []
    for setting in sport_settings:
        if "Run" in (setting.get("types") or []):
            lthr = setting.get("lthr")
            break
    if lthr is None and sport_settings:
        lthr = sport_settings[0].get("lthr")
    if lthr is None:
        lthr = context.profile_response.get("lthr") or context.profile_response.get("icu_lthr")

    assert lthr is not None and lthr > lthr_min, (
        f"Expected LTHR > {lthr_min}, received {lthr} in profile response: {context.profile_response.keys()}"
    )


@then("the event is listed in the staging athlete events")
def verify_event_listed(context: E2ETestContext) -> None:
    assert context.driver is not None
    event_id = context.created_event.get("id")
    assert event_id is not None, "Created event has no ID"

    today_str = datetime.date.today().isoformat()
    events = context.driver.get_events(oldest=today_str, newest=today_str)
    matched = [e for e in events if e.get("id") == event_id]
    assert len(matched) == 1, f"Event {event_id} not found in retrieved events: {events}"


@then("the test workout event is successfully deleted during cleanup")
def verify_event_deleted(context: E2ETestContext) -> None:
    assert context.driver is not None
    event_id = context.created_event.get("id")
    assert event_id is not None

    deleted = context.driver.delete_event(event_id)
    assert deleted is True, f"Failed to delete test event {event_id}"
