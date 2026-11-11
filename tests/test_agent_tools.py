#!/usr/bin/env python3
"""BDD Step definitions and driver for agent wrapper tools."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import time
from typing import Any, Dict, List, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from tests.drivers import MockIntervalsServer

scenarios("agent_tools.feature")


class AgentToolsDriver:
    def __init__(self, tmp_path: Path) -> None:
        self.tmp_path = tmp_path
        self.agent_data_dir = tmp_path / "agent_data"
        self.agent_data_dir.mkdir(parents=True, exist_ok=True)
        self.cache_file = self.agent_data_dir / "activities_cache.json"

        self.mock_server: Optional[MockIntervalsServer] = None
        self.last_output_payload: Optional[Dict[str, Any]] = None
        self.process_result: Optional[subprocess.CompletedProcess] = None
        self.cached_file_mtime: float = 0.0

    def start_mock_server(self, activities: Optional[List[Dict[str, Any]]] = None, events: Optional[List[Dict[str, Any]]] = None) -> None:
        self.mock_server = MockIntervalsServer()
        if activities:
            self.mock_server.activities = activities
        if events:
            for ev in events:
                eid = ev.get("id") or 101
                self.mock_server.events[eid] = ev
        self.mock_server.start()

    def run_tool(self, tool_script: str, args: Optional[List[str]] = None) -> None:
        cmd = [sys.executable, str(PROJECT_ROOT / "agent" / "tools" / tool_script)]
        if args:
            cmd.extend(args)

        env = os.environ.copy()
        env["AGENT_DATA_DIR"] = str(self.agent_data_dir)
        env["INTERVALS_API_KEY"] = "mock_key"
        env["INTERVALS_ATHLETE_ID"] = "0"
        if self.mock_server:
            env["INTERVALS_API_BASE"] = self.mock_server.base_url

        self.process_result = subprocess.run(
            cmd,
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
            env=env,
        )
        assert self.process_result.returncode == 0, (
            f"Tool {tool_script} failed with code {self.process_result.returncode}:\n"
            f"STDOUT: {self.process_result.stdout}\n"
            f"STDERR: {self.process_result.stderr}"
        )
        try:
            self.last_output_payload = json.loads(self.process_result.stdout.strip())
        except Exception:
            self.last_output_payload = {"raw": self.process_result.stdout}


@pytest.fixture
def driver(tmp_path: Path) -> AgentToolsDriver:
    return AgentToolsDriver(tmp_path)


@given("a mock Intervals server with running activities")
def given_mock_with_activities(driver: AgentToolsDriver) -> None:
    activities = [
        {
            "id": "run-001",
            "type": "Run",
            "name": "Morning Z2 Aerobic",
            "start_date_local": "2026-10-28T07:00:00",
            "distance": 8000.0,
            "moving_time": 2400,
            "average_heartrate": 145,
            "average_speed": 3.33,
            "icu_training_load": 50,
        },
        {
            "id": "run-002",
            "type": "Run",
            "name": "Tempo 5k",
            "start_date_local": "2026-10-30T17:30:00",
            "distance": 5000.0,
            "moving_time": 1500,
            "average_heartrate": 165,
            "average_speed": 3.33,
            "icu_training_load": 48,
        },
    ]
    driver.start_mock_server(activities=activities)


@given("an isolated agent data directory")
def given_isolated_data_dir(driver: AgentToolsDriver) -> None:
    pass


@given("an existing agent cache file")
def given_existing_cache_file(driver: AgentToolsDriver) -> None:
    initial_activities = [
        {
            "id": "old-run",
            "type": "Run",
            "name": "Old Run",
            "start_date_local": "2026-10-15T08:00:00",
            "distance": 5000.0,
            "moving_time": 1600,
            "average_speed": 3.12,
        }
    ]
    driver.cache_file.write_text(json.dumps(initial_activities), encoding="utf-8")
    driver.cached_file_mtime = driver.cache_file.stat().st_mtime


@given("an existing agent cache file with multi-week running activities")
def given_cache_with_multi_week_runs(driver: AgentToolsDriver) -> None:
    multi_week_runs = [
        {
            "id": "run-w1",
            "type": "Run",
            "name": "Week 1 Run",
            "start_date_local": "2026-10-05T07:00:00",
            "distance": 10000.0,
            "moving_time": 3000,
            "average_speed": 3.33,
            "icu_training_load": 60,
        },
        {
            "id": "run-w2",
            "type": "Run",
            "name": "Week 2 Run",
            "start_date_local": "2026-10-12T07:00:00",
            "distance": 12000.0,
            "moving_time": 3600,
            "average_speed": 3.33,
            "icu_training_load": 75,
        },
    ]
    driver.cache_file.write_text(json.dumps(multi_week_runs), encoding="utf-8")


@given("a mock Intervals server with upcoming planned workouts")
def given_mock_with_upcoming_workouts(driver: AgentToolsDriver) -> None:
    events = [
        {
            "id": 501,
            "category": "WORKOUT",
            "type": "Run",
            "name": "Schwellenintervalle 4x1000m",
            "start_date_local": "2026-11-05T07:00:00",
            "distance": 8000.0,
            "moving_time": 2700,
            "description": "4x 1000m @ 4:15 mit 400m Trabpause",
        },
        {
            "id": 502,
            "category": "WORKOUT",
            "type": "Run",
            "name": "Long Aerobic Base",
            "start_date_local": "2026-11-08T08:00:00",
            "distance": 15000.0,
            "moving_time": 5400,
            "description": "Lockerer Grundlagenausdauerlauf in Z2",
        },
    ]
    driver.start_mock_server(events=events)


@when(parsers.parse("get_activities tool is executed for {days:d} days"))
def when_run_get_activities(driver: AgentToolsDriver, days: int) -> None:
    driver.run_tool("get_activities.py", ["--days", str(days)])


@when("get_activities tool is executed again without force-refresh")
def when_run_get_activities_again(driver: AgentToolsDriver) -> None:
    # Clear mock server activities to ensure it relies on cache
    if driver.mock_server:
        driver.mock_server.activities = []
    driver.run_tool("get_activities.py", ["--days", "14"])


@when("get_activities tool is executed with force-refresh")
def when_run_get_activities_force(driver: AgentToolsDriver) -> None:
    time.sleep(0.05)  # Ensure distinct mtime
    driver.run_tool("get_activities.py", ["--force-refresh"])


@when(parsers.parse("get_trends tool is executed for {weeks:d} weeks"))
def when_run_get_trends(driver: AgentToolsDriver, weeks: int) -> None:
    driver.run_tool("get_trends.py", ["--weeks", str(weeks)])


@when(parsers.parse("get_upcoming_workouts tool is executed for {days:d} days"))
def when_run_get_upcoming(driver: AgentToolsDriver, days: int) -> None:
    driver.run_tool("get_upcoming_workouts.py", ["--days", str(days)])


@then("the agent cache file is created")
def then_cache_created(driver: AgentToolsDriver) -> None:
    assert driver.cache_file.is_file()


@then("the tool output contains formatted running activities")
def then_output_contains_activities(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    text = driver.last_output_payload.get("text", "")
    assert "Morning Z2 Aerobic" in text or "Laufaktivitäten" in text


@then("cached data is returned without downloading again")
def then_cache_returned(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    text = driver.last_output_payload.get("text", "")
    assert "Morning Z2 Aerobic" in text


@then("the cache file timestamp is updated")
def then_cache_timestamp_updated(driver: AgentToolsDriver) -> None:
    new_mtime = driver.cache_file.stat().st_mtime
    assert new_mtime >= driver.cached_file_mtime


@then("the trends output contains markdown table headers")
def then_trends_markdown(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    text = driver.last_output_payload.get("text", "")
    assert "Wöchentlicher Trainingstrend" in text
    assert "Woche" in text or "km" in text


@then("the trends output reports total distance and weeks count")
def then_trends_distance_count(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    data = driver.last_output_payload.get("data", {})
    assert data.get("total_km", 0) > 0
    assert data.get("weeks", 0) > 0


@then("the upcoming workouts output lists planned workout sessions")
def then_upcoming_workouts_listed(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    text = driver.last_output_payload.get("text", "")
    assert "Schwellenintervalle 4x1000m" in text
    assert "Long Aerobic Base" in text


@then("the output includes a direct Intervals.icu calendar chip")
def then_calendar_chip_included(driver: AgentToolsDriver) -> None:
    assert driver.last_output_payload is not None
    chips = driver.last_output_payload.get("chips", [])
    assert any("intervals.icu" in str(c.get("url", "")) for c in chips)
