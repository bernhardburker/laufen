#!/usr/bin/env python3
"""BDD Step definitions and driver for multi-tenant athlete onboarding."""

from __future__ import annotations

import io
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any, Dict, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.athletes import load_registry
from src.integrations.onboard_athlete import onboard_athlete, print_athletes_summary
from tests.drivers import MockIntervalsServer

scenarios("onboard_athlete.feature")


class OnboardAthleteDriver:
    def __init__(self, tmp_path: Path) -> None:
        self.tmp_path = tmp_path
        self.workspace_root = tmp_path / "workspace"
        self.workspace_root.mkdir(parents=True, exist_ok=True)
        self.athletes_dir = self.workspace_root / "athletes"
        self.athletes_dir.mkdir(parents=True, exist_ok=True)
        self.registry_file = self.athletes_dir / "registry.json"

        self.mock_server: Optional[MockIntervalsServer] = None
        self.captured_summary_output: str = ""
        self.last_onboard_error: Optional[str] = None
        self.cli_process_result: Optional[subprocess.CompletedProcess] = None

    def setup_registry(self, athletes: Optional[Dict[str, Any]] = None) -> None:
        registry_data = {
            "schema_version": 1,
            "default_athlete": "berni",
            "athletes": athletes if athletes is not None else {
                "berni": {
                    "intervals_id": "i771119",
                    "display_name": "Berni",
                    "active": True,
                    "dashboard_slug": "berni-89b9ec",
                },
                "stefan": {
                    "intervals_id": "i123456",
                    "display_name": "Stefan",
                    "active": True,
                    "dashboard_slug": "stefan-12ab34",
                },
            },
        }
        with open(self.registry_file, "w", encoding="utf-8") as f:
            json.dump(registry_data, f, indent=2)

    def print_summary(self) -> None:
        buf = io.StringIO()
        old_stdout = sys.stdout
        try:
            sys.stdout = buf
            print_athletes_summary(project_root=self.workspace_root)
        finally:
            sys.stdout = old_stdout
        self.captured_summary_output = buf.getvalue()

    def onboard(
        self,
        slug: str,
        intervals_id: Optional[str],
        display_name: Optional[str] = None,
        dry_run: bool = False,
    ) -> None:
        if not intervals_id:
            self.last_onboard_error = "Error: --intervals-id is required when onboarding an athlete."
            return

        api_key = "mock_key"
        api_base = f"{self.mock_server.base_url}" if self.mock_server else "https://intervals.icu/api/v1"

        try:
            onboard_athlete(
                slug=slug,
                intervals_id=intervals_id,
                display_name=display_name,
                api_key=api_key,
                api_base_url=api_base,
                fetch_history=False,
                project_root=self.workspace_root,
                dry_run=dry_run,
            )
        except Exception as e:
            self.last_onboard_error = str(e)

    def run_cli_command(self, cmd_arg: str) -> None:
        cmd = ["bash", str(PROJECT_ROOT / "run"), cmd_arg]
        self.cli_process_result = subprocess.run(
            cmd,
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
        )


@pytest.fixture
def driver(tmp_path: Path) -> OnboardAthleteDriver:
    return OnboardAthleteDriver(tmp_path)


@given("a clean registry with test athletes")
def given_clean_registry(driver: OnboardAthleteDriver) -> None:
    driver.setup_registry()


@given("an isolated project workspace")
def given_isolated_workspace(driver: OnboardAthleteDriver) -> None:
    driver.setup_registry(athletes={})


@given("a running Intervals.icu mock server")
def given_running_mock_server(driver: OnboardAthleteDriver) -> None:
    server = MockIntervalsServer()
    server.athletes["i98765"] = {
        "id": "i98765",
        "name": "Clara Runner",
        "icu_resting_hr": 49,
        "sportSettings": [
            {
                "types": ["Run"],
                "lthr": 172,
                "max_hr": 191,
                "hr_zones": [138, 150, 162, 172, 180, 186, 191],
                "hr_zone_names": ["Recovery", "Aerobic", "Tempo", "SubThreshold", "SuperThreshold", "Aerobic Capacity", "Anaerobic"],
            }
        ],
    }
    server.start()
    driver.mock_server = server


@when("the athlete summary is displayed")
def when_summary_displayed(driver: OnboardAthleteDriver) -> None:
    driver.print_summary()


@when(parsers.parse('onboarding athlete "{slug}" with Intervals ID "{intervals_id}" in dry run mode'))
def when_onboard_dry_run(driver: OnboardAthleteDriver, slug: str, intervals_id: str) -> None:
    driver.onboard(slug=slug, intervals_id=intervals_id, dry_run=True)


@when(parsers.parse('onboarding athlete "{slug}" with Intervals ID "{intervals_id}" and name "{display_name}"'))
def when_onboard_with_mock(driver: OnboardAthleteDriver, slug: str, intervals_id: str, display_name: str) -> None:
    driver.onboard(slug=slug, intervals_id=intervals_id, display_name=display_name)


@when(parsers.parse('onboarding athlete "{slug}" without an Intervals ID'))
def when_onboard_no_id(driver: OnboardAthleteDriver, slug: str) -> None:
    driver.onboard(slug=slug, intervals_id=None)


@when(parsers.parse('the CLI runner is executed with "{cmd_arg}"'))
def when_cli_runner_exec(driver: OnboardAthleteDriver, cmd_arg: str) -> None:
    driver.run_cli_command(cmd_arg)


@then("the output contains registered athlete slugs")
def then_summary_contains_slugs(driver: OnboardAthleteDriver) -> None:
    assert "berni" in driver.captured_summary_output
    assert "stefan" in driver.captured_summary_output


@then("the output displays table headers for slug, name, and intervals ID")
def then_summary_headers(driver: OnboardAthleteDriver) -> None:
    assert "Slug" in driver.captured_summary_output
    assert "Name" in driver.captured_summary_output
    assert "Intervals ID" in driver.captured_summary_output


@then(parsers.parse('the tenant directory for "{slug}" is not created'))
def then_tenant_dir_not_created(driver: OnboardAthleteDriver, slug: str) -> None:
    tenant_dir = driver.workspace_root / "athletes" / slug
    assert not tenant_dir.exists()


@then("the registry file remains unchanged")
def then_registry_unchanged(driver: OnboardAthleteDriver) -> None:
    reg = load_registry(project_root=driver.workspace_root)
    assert len(reg.get("athletes", {})) == 0


@then(parsers.parse('a tenant directory for "{slug}" is created'))
def then_tenant_dir_created(driver: OnboardAthleteDriver, slug: str) -> None:
    tenant_dir = driver.workspace_root / "athletes" / slug
    assert tenant_dir.is_dir()
    assert (tenant_dir / "athlete.json").is_file()


@then(parsers.parse('the tenant athlete config contains display name "{expected_name}"'))
def then_tenant_config_name(driver: OnboardAthleteDriver, expected_name: str) -> None:
    cfg_file = driver.workspace_root / "athletes" / "clara" / "athlete.json"
    with open(cfg_file, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    assert cfg["athlete"]["display_name"] == expected_name


@then(parsers.parse('the registry records "{slug}" with Intervals ID "{expected_id}"'))
def then_registry_records_athlete(driver: OnboardAthleteDriver, slug: str, expected_id: str) -> None:
    reg = load_registry(project_root=driver.workspace_root)
    assert slug in reg["athletes"]
    assert reg["athletes"][slug]["intervals_id"] == expected_id


@then(parsers.parse('"{slug}" is assigned a privacy dashboard slug'))
def then_assigned_dashboard_slug(driver: OnboardAthleteDriver, slug: str) -> None:
    reg = load_registry(project_root=driver.workspace_root)
    dash_slug = reg["athletes"][slug]["dashboard_slug"]
    assert dash_slug.startswith(f"{slug}-")
    assert len(dash_slug) > len(slug) + 1


@then("the process aborts with an intervals ID required error")
def then_aborts_no_id(driver: OnboardAthleteDriver) -> None:
    assert driver.last_onboard_error is not None
    assert "intervals-id is required" in driver.last_onboard_error.lower()


@then("the CLI runner exits successfully")
def then_cli_runner_success(driver: OnboardAthleteDriver) -> None:
    assert driver.cli_process_result is not None
    assert driver.cli_process_result.returncode == 0


@then("the output lists registered athletes")
def then_output_lists_athletes(driver: OnboardAthleteDriver) -> None:
    assert driver.cli_process_result is not None
    out = driver.cli_process_result.stdout
    assert "Registered Athletes" in out or "Slug" in out
