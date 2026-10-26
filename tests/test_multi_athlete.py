import http.server
import io
import json
import os
from pathlib import Path
import sys
import threading
from typing import Any, Dict, List, Optional
import pytest
from pytest_bdd import given, parsers, scenarios, then, when

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.athletes import (
    AthleteContext,
    list_registered_athletes,
    load_registry,
    print_shell_env,
    resolve_athlete_context,
    save_registry,
)
from src.integrations.onboard_athlete import onboard_athlete

# 1. Bind feature scenarios
scenarios("multi_athlete.feature")


# 2. Mock HTTP Server
class MockAthleteHandler(http.server.BaseHTTPRequestHandler):
    athlete_data = {
        "id": "i9999",
        "name": "Marcus",
        "icu_resting_hr": 52,
        "sportSettings": [
            {
                "types": ["Run"],
                "lthr": 170,
                "max_hr": 190,
                "hr_zones": [140, 152, 162, 172, 180, 186, 190],
                "hr_zone_names": [
                    "Recovery",
                    "Aerobic",
                    "Tempo",
                    "SubThreshold",
                    "SuperThreshold",
                    "Aerobic Capacity",
                    "Anaerobic",
                ],
            }
        ],
    }

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        if "/athlete/" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            if "/activities" in self.path or "/wellness" in self.path:
                self.wfile.write(b"[]")
            else:
                self.wfile.write(json.dumps(self.athlete_data).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        pass


# 3. Driver Class
class MultiAthleteDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.registry_path = tmp_path / "athletes" / "registry.json"
        self.env_overrides: Dict[str, str] = {}
        self.resolved_context: Optional[AthleteContext] = None
        self.athlete_list: List[AthleteContext] = []
        self.captured_shell_output: str = ""
        self.mock_server: Optional[http.server.HTTPServer] = None
        self.mock_server_thread: Optional[threading.Thread] = None
        self.mock_server_port: Optional[int] = None

    def start_mock_api(self) -> None:
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockAthleteHandler)
        self.mock_server_port = self.mock_server.server_address[1]
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_api(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def set_registry(self, data: Dict[str, Any]) -> None:
        self.registry_path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.registry_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    def remove_registry(self) -> None:
        if self.registry_path.is_file():
            self.registry_path.unlink()

    def set_env(self, key: str, value: str) -> None:
        self.env_overrides[key] = value
        os.environ[key] = value

    def clear_env(self) -> None:
        for k in list(self.env_overrides.keys()):
            os.environ.pop(k, None)
        self.env_overrides.clear()

    def resolve(self, athlete_slug: Optional[str] = None, allow_fallback: bool = True) -> None:
        self.resolved_context = resolve_athlete_context(
            athlete_slug=athlete_slug,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
            allow_legacy_fallback=allow_fallback,
        )

    def fetch_registered_list(self, active_only: bool = True) -> None:
        self.athlete_list = list_registered_athletes(
            project_root=self.tmp_path,
            registry_path=self.registry_path,
            active_only=active_only,
        )

    def capture_shell_env(self, slug: str) -> None:
        ctx = resolve_athlete_context(
            athlete_slug=slug,
            project_root=self.tmp_path,
            registry_path=self.registry_path,
        )
        old_stdout = sys.stdout
        sys.stdout = buffer = io.StringIO()
        try:
            print_shell_env(ctx)
            self.captured_shell_output = buffer.getvalue()
        finally:
            sys.stdout = old_stdout

    def onboard(self, slug: str, intervals_id: str, name: str) -> None:
        self.start_mock_api()
        try:
            onboard_athlete(
                slug=slug,
                intervals_id=intervals_id,
                display_name=name,
                api_key="mock_key",
                api_base_url=f"http://127.0.0.1:{self.mock_server_port}",
                fetch_history=True,
                project_root=self.tmp_path,
                dry_run=False,
            )
        finally:
            self.stop_mock_api()


# 4. Fixtures
@pytest.fixture
def driver(tmp_path: Path):
    d = MultiAthleteDriver(tmp_path)
    yield d
    d.clear_env()
    d.stop_mock_api()


# 5. Step Definitions
@given(parsers.parse('the athlete registry contains a default athlete "{default_slug}"'))
def registry_with_default(driver: MultiAthleteDriver, default_slug: str):
    driver.set_registry({
        "default_athlete": default_slug,
        "athletes": {
            default_slug: {
                "intervals_id": "i697752",
                "display_name": default_slug.capitalize(),
                "active": True,
                "dashboard_slug": default_slug,
            }
        },
    })


@given(parsers.parse('the athlete registry contains athlete "{slug}" with intervals id "{intervals_id}"'))
def registry_with_athlete(driver: MultiAthleteDriver, slug: str, intervals_id: str):
    driver.set_registry({
        "default_athlete": "berni",
        "athletes": {
            "berni": {
                "intervals_id": "i697752",
                "display_name": "Berni",
                "active": True,
                "dashboard_slug": "berni",
            },
            slug: {
                "intervals_id": intervals_id,
                "display_name": slug.capitalize(),
                "active": True,
                "dashboard_slug": f"{slug}-7f9c2a",
            },
        },
    })


@given(parsers.parse('the environment variable "{var_name}" is set to "{var_val}"'))
def set_env_var(driver: MultiAthleteDriver, var_name: str, var_val: str):
    driver.set_env(var_name, var_val)


@given("the athlete registry does not exist")
def registry_does_not_exist(driver: MultiAthleteDriver):
    driver.remove_registry()


@given(parsers.parse('a clean workspace without athlete "{slug}"'))
def clean_workspace_without_athlete(driver: MultiAthleteDriver, slug: str):
    driver.remove_registry()


@given(
    parsers.parse(
        'the athlete registry contains active athlete "{active_slug}" and inactive athlete "{inactive_slug}"'
    )
)
def registry_with_active_and_inactive(
    driver: MultiAthleteDriver, active_slug: str, inactive_slug: str
):
    driver.set_registry({
        "default_athlete": active_slug,
        "athletes": {
            active_slug: {
                "intervals_id": "i111",
                "display_name": active_slug.capitalize(),
                "active": True,
                "dashboard_slug": active_slug,
            },
            inactive_slug: {
                "intervals_id": "i222",
                "display_name": inactive_slug.capitalize(),
                "active": False,
                "dashboard_slug": inactive_slug,
            },
        },
    })


@when("the athlete context is resolved without an explicit athlete argument")
def resolve_context_without_arg(driver: MultiAthleteDriver):
    driver.resolve(athlete_slug=None)


@when(parsers.parse('the athlete context is resolved for athlete "{slug}"'))
def resolve_context_for_athlete(driver: MultiAthleteDriver, slug: str):
    driver.resolve(athlete_slug=slug)


@when("the athlete context is resolved with fallback enabled")
def resolve_context_with_fallback(driver: MultiAthleteDriver):
    driver.resolve(athlete_slug=None, allow_fallback=True)


@when("active registered athletes are listed")
def list_active_athletes(driver: MultiAthleteDriver):
    driver.fetch_registered_list(active_only=True)


@when(parsers.parse('the athlete "{slug}" is onboarded with intervals id "{intervals_id}" and name "{name}"'))
def onboard_new_athlete(driver: MultiAthleteDriver, slug: str, intervals_id: str, name: str):
    driver.onboard(slug, intervals_id, name)


@when(parsers.parse('shell environment is generated for athlete "{slug}"'))
def generate_shell_env(driver: MultiAthleteDriver, slug: str):
    driver.capture_shell_env(slug)


@then(parsers.parse('the resolved athlete slug is "{expected_slug}"'))
def verify_resolved_slug(driver: MultiAthleteDriver, expected_slug: str):
    assert driver.resolved_context is not None
    assert driver.resolved_context.slug == expected_slug


@then(parsers.parse('the athlete base directory points to "{expected_dir}"'))
def verify_base_dir(driver: MultiAthleteDriver, expected_dir: str):
    assert driver.resolved_context is not None
    expected_path = driver.tmp_path / expected_dir
    assert driver.resolved_context.base_dir == expected_path


@then(parsers.parse('the athlete activities path points to "{expected_path}"'))
def verify_activities_path(driver: MultiAthleteDriver, expected_path: str):
    assert driver.resolved_context is not None
    expected = driver.tmp_path / expected_path
    assert driver.resolved_context.activities_path == expected


@then(parsers.parse('the resolved athlete intervals id is "{expected_id}"'))
def verify_intervals_id(driver: MultiAthleteDriver, expected_id: str):
    assert driver.resolved_context is not None
    assert driver.resolved_context.intervals_id == expected_id


@then(parsers.parse('the dashboard slug is "{expected_dash_slug}"'))
def verify_dash_slug(driver: MultiAthleteDriver, expected_dash_slug: str):
    assert driver.resolved_context is not None
    assert driver.resolved_context.dashboard_slug == expected_dash_slug


@then(parsers.parse('the athlete config path points to "{expected_config}"'))
def verify_config_path(driver: MultiAthleteDriver, expected_config: str):
    assert driver.resolved_context is not None
    expected = driver.tmp_path / expected_config
    assert driver.resolved_context.athlete_config_path == expected


@then(parsers.parse('the list contains "{slug}"'))
def verify_list_contains(driver: MultiAthleteDriver, slug: str):
    slugs = [a.slug for a in driver.athlete_list]
    assert slug in slugs


@then(parsers.parse('the list does not contain "{slug}"'))
def verify_list_not_contains(driver: MultiAthleteDriver, slug: str):
    slugs = [a.slug for a in driver.athlete_list]
    assert slug not in slugs


@then(parsers.parse('the athlete registry contains athlete "{slug}" with intervals id "{intervals_id}"'))
def verify_registry_contains_athlete(driver: MultiAthleteDriver, slug: str, intervals_id: str):
    reg = load_registry(driver.registry_path, driver.tmp_path)
    athletes = reg.get("athletes", {})
    assert slug in athletes
    assert athletes[slug]["intervals_id"] == intervals_id


@then(parsers.parse('the shell export output contains \'{expected_snippet}\''))
def verify_shell_output(driver: MultiAthleteDriver, expected_snippet: str):
    assert expected_snippet in driver.captured_shell_output
